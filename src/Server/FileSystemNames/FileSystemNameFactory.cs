﻿// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System.ComponentModel.Composition;
using VsChromium.Core.Files;

namespace VsChromium.Server.FileSystemNames {
  [Export(typeof(IFileSystemNameFactory))]
  public class FileSystemNameFactory : IFileSystemNameFactory {

    public DirectoryName CreateAbsoluteDirectoryName(FullPath path) {
      return new AbsoluteDirectoryName(path);
    }

    public FileName CreateFileName(DirectoryName parent, string name) {
      return new FileName(parent, name);
    }

    public DirectoryName CreateDirectoryName(DirectoryName parent, string name) {
      return new RelativeDirectoryName(parent, name);
    }
  }
}
