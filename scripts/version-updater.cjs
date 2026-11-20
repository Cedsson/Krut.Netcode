'use strict'

// A "bump updater" for standard-version. It reads and writes the version number in
// src/Krut.Netcode/Krut.Netcode.csproj, which is the project's single source of truth
// for the version.

const VERSION_PATTERN = /<Version>([^<]*)<\/Version>/

function readVersion (contents) {
  const match = VERSION_PATTERN.exec(contents)
  if (!match) {
    throw new Error(
      'Could not find <Version>...</Version> in the csproj file. ' +
      'Check that <Version> is present inside a PropertyGroup in src/Krut.Netcode/Krut.Netcode.csproj.'
    )
  }

  const version = match[1].trim()
  if (version === '') {
    throw new Error('<Version> in the csproj file is empty. Set a version number before releasing.')
  }

  return version
}

function writeVersion (contents, version) {
  if (!VERSION_PATTERN.test(contents)) {
    throw new Error(
      'Could not find <Version>...</Version> in the csproj file - see readVersion().'
    )
  }

  return contents.replace(VERSION_PATTERN, '<Version>' + version + '</Version>')
}

module.exports = { readVersion, writeVersion }
