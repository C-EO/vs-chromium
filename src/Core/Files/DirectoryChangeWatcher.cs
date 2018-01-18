﻿// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using VsChromium.Core.Linq;
using VsChromium.Core.Logging;
using VsChromium.Core.Threads;
using VsChromium.Core.Utility;

namespace VsChromium.Core.Files {
  public class DirectoryChangeWatcher : IDirectoryChangeWatcher {
    /// <summary>
    /// Record the last 100 change notification, for debugging purpose only.
    /// </summary>
    private static readonly PathChangeRecorder GlobalChangeRecorder = new PathChangeRecorder();
    private readonly IFileSystem _fileSystem;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly AutoResetEvent _eventReceived = new AutoResetEvent(false);
    private readonly PollingDelayPolicy _pathChangesPolling;
    private readonly PollingDelayPolicy _simplePathChangesPolling;
    private readonly PollingDelayPolicy _checkRootsPolling;
    private readonly BoundedOperationLimiter _logLimiter = new BoundedOperationLimiter(10);

    /// <summary>
    /// Dictionary of watchers, one per root directory path.
    /// </summary>
    private readonly Dictionary<FullPath, DirectoryWatcherhEntry> _watchers = new Dictionary<FullPath, DirectoryWatcherhEntry>();
    private readonly object _watchersLock = new object();

    /// <summary>
    /// Dictionary of file change events, per path.
    /// </summary>
    private Dictionary<FullPath, PathChangeEntry> _changedPaths = new Dictionary<FullPath, PathChangeEntry>();
    private readonly object _changedPathsLock = new object();

    /// <summary>
    /// The polling and event posting thread.
    /// </summary>
    private readonly TimeSpan _pollingThreadTimeout = TimeSpan.FromSeconds(1.0);
    private Thread _pollingThread;

    public DirectoryChangeWatcher(IFileSystem fileSystem, IDateTimeProvider dateTimeProvider) {
      _fileSystem = fileSystem;
      _dateTimeProvider = dateTimeProvider;
      _simplePathChangesPolling = new PollingDelayPolicy(dateTimeProvider, TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(10.0));
      _pathChangesPolling = new PollingDelayPolicy(dateTimeProvider, TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(60.0));
      _checkRootsPolling = new PollingDelayPolicy(dateTimeProvider, TimeSpan.FromSeconds(15.0), TimeSpan.FromSeconds(60.0));
    }

    private class DirectoryWatcherhEntry {
      public FullPath Path { get; set; }
      public IFileSystemWatcher DirectoryNameWatcher { get; set; }
      public IFileSystemWatcher FileNameWatcher { get; set; }
      public IFileSystemWatcher FileWriteWatcher { get; set; }

      public void Dispose() {
        DirectoryNameWatcher?.Dispose();
        FileNameWatcher?.Dispose();
        FileWriteWatcher.Dispose();
      }

      public void Start() {
        DirectoryNameWatcher?.Start();
        FileNameWatcher?.Start();
        FileWriteWatcher?.Start();
      }

      public void Stop() {
        DirectoryNameWatcher?.Stop();
        FileNameWatcher?.Stop();
        FileWriteWatcher?.Stop();
      }
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private static void LogPathForDebugging(string path, PathChangeKind kind, PathKind pathKind) {
#if false
      var pathToLog = @"";
      if (SystemPathComparer.Instance.IndexOf(path, pathToLog, 0, path.Length) == 0) {
        Logger.LogInfo("*************************** {0}: {1}-{2} *******************", path, kind, pathKind);
      }
#endif
    }

    public void WatchDirectories(IEnumerable<FullPath> directories) {
      lock (_watchersLock) {
        var oldSet = new HashSet<FullPath>(_watchers.Keys);
        var newSet = new HashSet<FullPath>(directories);

        var removed = new HashSet<FullPath>(oldSet);
        removed.ExceptWith(newSet);

        var added = new HashSet<FullPath>(newSet);
        added.ExceptWith(oldSet);

        removed.ForAll(RemoveDirectory);
        added.ForAll(AddDirectory);
      }
    }

    public void Start() {
      lock (_watchersLock) {
        foreach (var watcher in _watchers) {
          watcher.Value.Start();
        }
      }
    }

    public void Stop() {
      lock (_watchersLock) {
        foreach (var watcher in _watchers) {
          watcher.Value.Stop();
        }
      }
    }

    public event Action<IList<PathChangeEntry>> PathsChanged;
    public event Action<Exception> Error;

    protected virtual void OnError(Exception obj) {
      Error?.Invoke(obj);
    }

    private void AddDirectory(FullPath directory) {
      DirectoryWatcherhEntry watcherEntry;
      lock (_watchersLock) {
        if (_pollingThread == null) {
          _pollingThread = new Thread(ThreadLoop) {IsBackground = true};
          _pollingThread.Start();
        }
        if (_watchers.TryGetValue(directory, out watcherEntry))
          return;

        watcherEntry = new DirectoryWatcherhEntry {
          Path = directory,
          DirectoryNameWatcher = _fileSystem.CreateDirectoryWatcher(directory),
          FileNameWatcher = _fileSystem.CreateDirectoryWatcher(directory),
          FileWriteWatcher = _fileSystem.CreateDirectoryWatcher(directory),
        };
        _watchers.Add(directory, watcherEntry);
      }

      Logger.LogInfo("Starting monitoring directory \"{0}\" for change notifications.", directory);

      // Note: "DirectoryName" captures directory creation, deletion and rename
      watcherEntry.DirectoryNameWatcher.NotifyFilter = NotifyFilters.DirectoryName;
      watcherEntry.DirectoryNameWatcher.Changed += (s, e) => WatcherOnChanged(s, e, PathKind.Directory);
      watcherEntry.DirectoryNameWatcher.Created += (s, e) => WatcherOnCreated(s, e, PathKind.Directory);
      watcherEntry.DirectoryNameWatcher.Deleted += (s, e) => WatcherOnDeleted(s, e, PathKind.Directory);
      watcherEntry.DirectoryNameWatcher.Renamed += (s, e) => WatcherOnRenamed(s, e, PathKind.Directory);

      // Note: "FileName" captures file creation, deletion and rename
      watcherEntry.FileNameWatcher.NotifyFilter = NotifyFilters.FileName;
      watcherEntry.FileNameWatcher.Changed += (s, e) => WatcherOnChanged(s, e, PathKind.File);
      watcherEntry.FileNameWatcher.Created += (s, e) => WatcherOnCreated(s, e, PathKind.File);
      watcherEntry.FileNameWatcher.Deleted += (s, e) => WatcherOnDeleted(s, e, PathKind.File);
      watcherEntry.FileNameWatcher.Renamed += (s, e) => WatcherOnRenamed(s, e, PathKind.File);

      // Note: "LastWrite" will catch changes to *both* files and directories, i.e. it is
      // not possible to known which one it is.
      // For directories, a "LastWrite" change occurs when a child entry (file or directory) is added,
      // renamed or deleted.
      // For files, a "LastWrite" change occurs when the file is written to.
      watcherEntry.FileWriteWatcher.NotifyFilter = NotifyFilters.LastWrite;
      watcherEntry.FileWriteWatcher.Changed += (s, e) => WatcherOnChanged(s, e, PathKind.FileOrDirectory);
      watcherEntry.FileWriteWatcher.Created += (s, e) => WatcherOnCreated(s, e, PathKind.FileOrDirectory);
      watcherEntry.FileWriteWatcher.Deleted += (s, e) => WatcherOnDeleted(s, e, PathKind.FileOrDirectory);
      watcherEntry.FileWriteWatcher.Renamed += (s, e) => WatcherOnRenamed(s, e, PathKind.FileOrDirectory);

      foreach (var watcher in new[]
        {watcherEntry.DirectoryNameWatcher, watcherEntry.FileNameWatcher, watcherEntry.FileWriteWatcher}) {
        watcher.IncludeSubdirectories = true;
        // Note: The MSDN documentation says to use less than 64KB
        //       (see https://msdn.microsoft.com/en-us/library/system.io.filesystemwatcher.internalbuffersize(v=vs.110).aspx)
        //         "You can set the buffer to 4 KB or larger, but it must not exceed 64 KB."
        //       However, the implementation allows for arbitrary buffer sizes.
        //       Experience has shown that 64KB is small enough that we frequently run into "OverflowException"
        //       exceptions on heavily active file systems (e.g. during a build of a complex project
        //       such as Chromium).
        //       The issue with these exceptions is that the consumer must be extremely conservative
        //       when such errors occur, because we lost track of what happened at the individual
        //       directory/file level. In the case of VsChromium, the server will batch a full re-scan
        //       of the file system, instead of an incremental re-scan, and that can be quite time
        //       consuming (as well as I/O consuming).
        //       In the end, increasing the size of the buffer to 2 MB is the best option to avoid
        //       these issues (2 MB is not that much memory in the grand scheme of things).
        //watcher.InternalBufferSize = 2 * 1024 * 1024; // 2 MB
        watcher.InternalBufferSize = 8 * 1024; // 8 KB
        watcher.Error += WatcherOnError;
        watcher.Start();
      }
    }

    private void RemoveDirectory(FullPath directory) {
      DirectoryWatcherhEntry watcher;
      lock (_watchersLock) {
        if (!_watchers.TryGetValue(directory, out watcher))
          return;
        _watchers.Remove(directory);
      }
      Logger.LogInfo("Removing directory \"{0}\" from change notification monitoring.", directory);
      watcher.Dispose();
    }

    private void ThreadLoop() {
      Logger.LogInfo("Starting directory change notification monitoring thread.");
      try {
        while (true) {
          _eventReceived.WaitOne(_pollingThreadTimeout);

          CheckDeletedRoots();
          PostPathsChangedEvents();
        }
      }
      catch (Exception e) {
        Logger.LogError(e, "Error in DirectoryChangeWatcher.");
      }
    }

    /// <summary>
    /// The OS FileSystem notification does not notify us if a directory used for
    /// change notification is deleted (or renamed). We have to use polling to detect
    /// this kind of changes.
    /// </summary>
    private void CheckDeletedRoots() {
      Debug.Assert(_pollingThread == Thread.CurrentThread);
      if (!_checkRootsPolling.WaitTimeExpired())
        return;
      _checkRootsPolling.Restart();

      lock (_watchersLock) {
        var deletedWatchers = _watchers
          .Where(item => !_fileSystem.DirectoryExists(item.Key))
          .ToList();

        deletedWatchers
          .ForAll(item => {
            EnqueueChangeEvent(item.Key, RelativePath.Empty, PathChangeKind.Deleted, PathKind.Directory);
            RemoveDirectory(item.Key);
          });
      }
    }

    private void PostPathsChangedEvents() {
      Debug.Assert(_pollingThread == Thread.CurrentThread);
      var changedPaths = DequeueChangedPathsEvents();

      // Dequeue events as long as there are new ones showing up as we wait for
      // our polling delays to expire.
      // The goal is to delay generating events as long as there is disk
      // activity within a 10 seconds window. It also allows "merging"
      // consecutive events into more meaningful ones.
      while (changedPaths.Count > 0) {

        // Post changes that belong to an expired polling interval.
        if (_simplePathChangesPolling.WaitTimeExpired()) {
          PostPathsChangedEvents(changedPaths, x => x == PathChangeKind.Changed);
          _simplePathChangesPolling.Restart();
        }
        if (_pathChangesPolling.WaitTimeExpired()) {
          PostPathsChangedEvents(changedPaths, x => true);
          _pathChangesPolling.Restart();
        }

        // If we are done, exit to waiting thread
        if (changedPaths.Count == 0)
          break;

        // If there are leftover paths, this means some polling interval(s) have
        // not expired. Go back to sleeping for a little bit or until we receive
        // a new change event.
        _eventReceived.WaitOne(_pollingThreadTimeout);

        // See if we got new events, and merge them.
        var morePathsChanged = DequeueChangedPathsEvents();
        morePathsChanged.ForAll(change => MergePathChange(changedPaths, change.Value));

        // If we got more changes, reset the polling interval for the non-simple
        // path changed. The goal is to avoid processing those too frequently if
        // there is activity on disk (e.g. a build happening), because
        // processing add/delete changes is currently much more expensive in the
        // search engine file database.
        // Note we don't update the simple "file change" events, as those as cheaper
        // to process.
        if (morePathsChanged.Count > 0) {
          _simplePathChangesPolling.Checkpoint();
          _pathChangesPolling.Checkpoint();
        }
      }

      // We are done processing all changes, make sure we wait at least some
      // amount of time before processing anything more.
      _simplePathChangesPolling.Restart();
      _pathChangesPolling.Restart();

      Debug.Assert(changedPaths.Count == 0);
    }

    /// <summary>
    /// Filter, remove and post events for all changes in <paramref
    /// name="paths"/> that match <paramref name="predicate"/>
    /// </summary>
    private void PostPathsChangedEvents(IDictionary<FullPath, PathChangeEntry> paths, Func<PathChangeKind, bool> predicate) {
      RemoveIgnorableEvents(paths);

      var changes = paths
        .Where(x => predicate(x.Value.ChangeKind))
        .Select(x => x.Value)
        .ToList();
      if (changes.Count == 0)
        return;

      changes.ForAll(x => paths.Remove(x.Path));
      OnPathsChanged(changes);
    }

    private bool IncludeChange(PathChangeEntry entry) {
      // Ignore changes for files that have been created then deleted
      if (entry.ChangeKind == PathChangeKind.None)
        return false;


      return true;
    }

    private IDictionary<FullPath, PathChangeEntry> DequeueChangedPathsEvents() {
      // Copy current changes into temp and reset to empty collection.
      lock (_changedPathsLock) {
        var temp = _changedPaths;
        _changedPaths = new Dictionary<FullPath, PathChangeEntry>();
        return temp;
      }
    }

    private void RemoveIgnorableEvents(IDictionary<FullPath, PathChangeEntry> changes) {
      changes.RemoveWhere(x => !IncludeChange(x.Value));
    }

    private void EnqueueChangeEvent(FullPath rootPath, RelativePath entryPath, PathChangeKind changeKind, PathKind pathKind) {
      //Logger.LogInfo("Enqueue change event: {0}, {1}", path, changeKind);
      var entry = new PathChangeEntry(rootPath, entryPath, changeKind, pathKind);
      GlobalChangeRecorder.RecordChange(new PathChangeRecorder.ChangeInfo {
        Entry = entry,
        TimeStampUtc = _dateTimeProvider.UtcNow,
      });

      lock (_changedPathsLock) {
        MergePathChange(_changedPaths, entry);
      }
    }

    private static void MergePathChange(IDictionary<FullPath, PathChangeEntry> changes, PathChangeEntry entry) {
      var currentChangeKind = PathChangeKind.None;
      var currentPathKind = PathKind.FileOrDirectory;
      PathChangeEntry currentEntry;
      if (changes.TryGetValue(entry.Path, out currentEntry)) {
        currentChangeKind = currentEntry.ChangeKind;
        currentPathKind = currentEntry.PathKind;
      }
      changes[entry.Path] = new PathChangeEntry(
        entry.BasePath,
        entry.RelativePath,
        CombineChangeKinds(currentChangeKind, entry.ChangeKind),
        CombinePathKind(currentPathKind, entry.PathKind));
    }

    private static PathKind CombinePathKind(PathKind current, PathKind next) {
      switch (current) {
        case PathKind.File:
          switch (next) {
            case PathKind.File: return PathKind.File;
            case PathKind.Directory: return PathKind.FileAndDirectory;
            case PathKind.FileOrDirectory: return PathKind.File;
            case PathKind.FileAndDirectory: return PathKind.FileAndDirectory;
            default: throw new ArgumentOutOfRangeException("next");
          }
        case PathKind.Directory:
          switch (next) {
            case PathKind.File: return PathKind.FileAndDirectory;
            case PathKind.Directory: return PathKind.Directory;
            case PathKind.FileOrDirectory: return PathKind.Directory;
            case PathKind.FileAndDirectory: return PathKind.FileAndDirectory;
            default: throw new ArgumentOutOfRangeException("next");
          }
        case PathKind.FileOrDirectory:
          switch (next) {
            case PathKind.File: return PathKind.File;
            case PathKind.Directory: return PathKind.Directory;
            case PathKind.FileOrDirectory: return PathKind.FileOrDirectory;
            case PathKind.FileAndDirectory: return PathKind.FileAndDirectory;
            default: throw new ArgumentOutOfRangeException("next");
          }
        case PathKind.FileAndDirectory:
          switch (next) {
            case PathKind.File: return PathKind.FileAndDirectory;
            case PathKind.Directory: return PathKind.FileAndDirectory;
            case PathKind.FileOrDirectory: return PathKind.FileAndDirectory;
            case PathKind.FileAndDirectory: return PathKind.FileAndDirectory;
            default: throw new ArgumentOutOfRangeException("next");
          }
        default:
          throw new ArgumentOutOfRangeException("current");
      }
    }

    private static PathChangeKind CombineChangeKinds(PathChangeKind current, PathChangeKind next) {
      switch (current) {
        case PathChangeKind.None:
          return next;
        case PathChangeKind.Created:
          switch (next) {
            case PathChangeKind.None:
              return current;
            case PathChangeKind.Created:
              return current;
            case PathChangeKind.Deleted:
              return PathChangeKind.None;
            case PathChangeKind.Changed:
              return current;
            default:
              throw new ArgumentOutOfRangeException("next");
          }
        case PathChangeKind.Deleted:
          switch (next) {
            case PathChangeKind.None:
              return current;
            case PathChangeKind.Created:
              return PathChangeKind.Changed;
            case PathChangeKind.Deleted:
              return current;
            case PathChangeKind.Changed:
              return PathChangeKind.Deleted; // Weird case...
            default:
              throw new ArgumentOutOfRangeException("next");
          }
        case PathChangeKind.Changed:
          switch (next) {
            case PathChangeKind.None:
              return current;
            case PathChangeKind.Created:
              return PathChangeKind.Changed; // Weird case...
            case PathChangeKind.Deleted:
              return next;
            case PathChangeKind.Changed:
              return current;
            default:
              throw new ArgumentOutOfRangeException("next");
          }
        default:
          throw new ArgumentOutOfRangeException("current");
      }
    }

    /// <summary>
    ///  Skip paths BCL can't process (e.g. path too long)
    /// </summary>
    private bool SkipPath(string path) {
      if (PathHelpers.IsPathTooLong(path)) {
        switch (_logLimiter.Proceed()) {
          case BoundedOperationLimiter.Result.YesAndLast:
            Logger.LogInfo("(The following log message will be the last of its kind)", path);
            goto case BoundedOperationLimiter.Result.Yes;
          case BoundedOperationLimiter.Result.Yes:
            Logger.LogInfo("Skipping file change event because path is too long: \"{0}\"", path);
            break;
          case BoundedOperationLimiter.Result.NoMore:
            break;
        }
        return true;
      }
      if (!PathHelpers.IsValidBclPath(path)) {
        switch (_logLimiter.Proceed()) {
          case BoundedOperationLimiter.Result.YesAndLast:
            Logger.LogInfo("(The following log message will be the last of its kind)", path);
            goto case BoundedOperationLimiter.Result.Yes;
          case BoundedOperationLimiter.Result.Yes:
            Logger.LogInfo("Skipping file change event because path is invalid: \"{0}\"", path);
            break;
          case BoundedOperationLimiter.Result.NoMore:
            break;
        }
        return true;
      }
      return false;
    }

    private void WatcherOnError(object sender, ErrorEventArgs errorEventArgs) {
      Logger.WrapActionInvocation(() => {
        // TODO(rpaquay): Try to recover?
        Logger.LogError(errorEventArgs.GetException(), "File system watcher for path \"{0}\" error.",
          ((FileSystemWatcher)sender).Path);
        OnError(errorEventArgs.GetException());
      });
    }

    private void WatcherOnChanged(object sender, FileSystemEventArgs args, PathKind pathKind) {
      Logger.WrapActionInvocation(() => {
        var watcher = (FileSystemWatcher)sender;

        var path = PathHelpers.CombinePaths(watcher.Path, args.Name);
        LogPathForDebugging(path, PathChangeKind.Changed, pathKind);
        if (SkipPath(path))
          return;

        EnqueueChangeEvent(new FullPath(watcher.Path), new RelativePath(args.Name), PathChangeKind.Changed, pathKind);
        _eventReceived.Set();
      });
    }

    private void WatcherOnCreated(object sender, FileSystemEventArgs args, PathKind pathKind) {
      Logger.WrapActionInvocation(() => {
        var watcher = (FileSystemWatcher)sender;

        var path = PathHelpers.CombinePaths(watcher.Path, args.Name);
        LogPathForDebugging(path, PathChangeKind.Created, pathKind);
        if (SkipPath(path))
          return;

        EnqueueChangeEvent(new FullPath(watcher.Path), new RelativePath(args.Name), PathChangeKind.Created, pathKind);
        _eventReceived.Set();
      });
    }

    private void WatcherOnDeleted(object sender, FileSystemEventArgs args, PathKind pathKind) {
      Logger.WrapActionInvocation(() => {
        var watcher = (FileSystemWatcher)sender;

        var path = PathHelpers.CombinePaths(watcher.Path, args.Name);
        LogPathForDebugging(path, PathChangeKind.Deleted, pathKind);
        if (SkipPath(path))
          return;

        EnqueueChangeEvent(new FullPath(watcher.Path), new RelativePath(args.Name), PathChangeKind.Deleted, pathKind);
        _eventReceived.Set();
      });
    }

    private void WatcherOnRenamed(object sender, RenamedEventArgs args, PathKind pathKind) {
      Logger.WrapActionInvocation(() => {
        var watcher = (FileSystemWatcher)sender;

        var path = PathHelpers.CombinePaths(watcher.Path, args.Name);
        LogPathForDebugging(path, PathChangeKind.Created, pathKind);
        if (SkipPath(path))
          return;

        var oldPath = PathHelpers.CombinePaths(watcher.Path, args.OldName);
        LogPathForDebugging(oldPath, PathChangeKind.Deleted, pathKind);
        if (SkipPath(oldPath))
          return;

        EnqueueChangeEvent(new FullPath(watcher.Path), new RelativePath(args.OldName), PathChangeKind.Deleted, pathKind);
        EnqueueChangeEvent(new FullPath(watcher.Path), new RelativePath(args.Name), PathChangeKind.Created, pathKind);
        _eventReceived.Set();
      });
    }

    /// <summary>
    /// Executed on the background thread when changes need to be notified to
    /// our listeners.
    /// </summary>
    protected virtual void OnPathsChanged(IList<PathChangeEntry> changes) {
      if (changes.Count == 0)
        return;

      //Logger.LogInfo("DirectoryChangedWatcher.OnPathsChanged: {0} items (logging max 5 below).", changes.Count);
      //changes.Take(5).ForAll(x => 
      //  Logger.LogInfo("  Path changed: \"{0}\", {1}.", x.Path, x.ChangeKind));
      var handler = PathsChanged;
      if (handler != null)
        handler(changes);
    }

    private class PollingDelayPolicy {
      private readonly IDateTimeProvider _dateTimeProvider;
      private readonly TimeSpan _checkpointDelay;
      private readonly TimeSpan _maxDelay;
      private DateTime _lastPollUtc;
      private DateTime _lastCheckpointUtc;

      private static class ClassLogger {
        static ClassLogger() {
#if DEBUG
          //LogInfoEnabled = true;
#endif
        }
        // ReSharper disable once UnusedAutoPropertyAccessor.Local
        public static bool LogInfoEnabled { get; set; }
      }

      public PollingDelayPolicy(IDateTimeProvider dateTimeProvider, TimeSpan checkpointDelay, TimeSpan maxDelay) {
        _dateTimeProvider = dateTimeProvider;
        _checkpointDelay = checkpointDelay;
        _maxDelay = maxDelay;
        Restart();
      }

      /// <summary>
      /// Called when all events have been flushed, resets all timers.
      /// </summary>
      public void Restart() {
        _lastPollUtc = _lastCheckpointUtc = _dateTimeProvider.UtcNow;
      }

      /// <summary>
      /// Called when a new event instance occurred, resets the "checkpoint"
      /// timer.
      /// </summary>
      public void Checkpoint() {
        _lastCheckpointUtc = _dateTimeProvider.UtcNow;
      }

      /// <summary>
      /// Returns <code>true</code> when either the maxmium or checkpoint delay
      /// has expired.
      /// </summary>
      public bool WaitTimeExpired() {
        var now = _dateTimeProvider.UtcNow;

        var result = (now - _lastPollUtc >= _maxDelay) ||
                     (now - _lastCheckpointUtc >= _checkpointDelay);
        if (result) {
          if (ClassLogger.LogInfoEnabled) {
            Logger.LogInfo("Timer expired: now={0}, checkpoint={1} msec, start={2} msec, checkpointDelay={3:n0} msec, maxDelay={4:n0} msec",
              now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
              (now - _lastCheckpointUtc).TotalMilliseconds,
              (now - _lastPollUtc).TotalMilliseconds,
              _checkpointDelay.TotalMilliseconds,
              _maxDelay.TotalMilliseconds);
          }
        }
        return result;
      }
    }

    private class PathChangeRecorder {
      private readonly ConcurrentQueue<ChangeInfo> _lastRecords = new ConcurrentQueue<ChangeInfo>();

      public void RecordChange(ChangeInfo entry) {
        if (_lastRecords.Count >= 100) {
          ChangeInfo temp;
          _lastRecords.TryDequeue(out temp);
        }
        _lastRecords.Enqueue(entry);
      }

      public class ChangeInfo {
        public PathChangeEntry Entry { get; set; }
        public DateTime TimeStampUtc { get; set; }
      }
    }
  }
}
