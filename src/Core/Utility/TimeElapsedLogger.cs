// Copyright 2015 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Diagnostics;
using VsChromium.Core.Logging;

namespace VsChromium.Core.Utility {
  /// <summary>
  /// Utility class to measure and log time spent in a block of code typically
  /// wrapped with a "using" statement of an instance of this class.
  /// </summary>
  public struct TimeElapsedLogger : IDisposable {
    [ThreadStatic]
    private static int _currentThreadIndent;

    private readonly string _description;
    private readonly Stopwatch _stopwatch;
    private readonly string _indent;

    public TimeElapsedLogger(string description) {
      _currentThreadIndent++;
      _description = description;
      _stopwatch = Stopwatch.StartNew();
      _indent = GetIndent(_currentThreadIndent);
      if (Logger.IsDebugEnabled) {
        Logger.LogDebug("{0}{1}.", _indent, _description);
      }
    }

    public void Dispose() {
      _currentThreadIndent--;
      _stopwatch.Stop();
      if (Logger.IsDebugEnabled) {
        Logger.LogDebug(
          "{0}{1} performed in {2:n0} msec - GC Memory: {3:n0} bytes.",
          _indent,
          _description,
          _stopwatch.ElapsedMilliseconds,
          GC.GetTotalMemory(false));
      }
    }

    public string Indent {
      get { return _indent; }
    }

    public static string GetIndent(int indent) {
      switch (indent) {
        case 0:
          return "";
        case 1:
          return ">> ";
        case 2:
          return ">>>> ";
        case 3:
          return ">>>>>> ";
        case 4:
          return ">>>>>>>> ";
        case 5:
          return ">>>>>>>>>> ";
        case 6:
          return ">>>>>>>>>>>> ";
        default:
          return new string('>', indent * 2);
      }
    }
  }
}