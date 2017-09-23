﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//-----------------------------------------------------------------------
// </copyright>
// <summary>Functions for matching file names with patterns.</summary>
//-----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Globalization;
using System.Collections.Generic;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// Functions for matching file names with patterns. 
    /// </summary>
    internal static class FileMatcher
    {
        private const string recursiveDirectoryMatch = "**";
        private const string dotdot = "..";

        private static readonly string s_directorySeparator = new string(Path.DirectorySeparatorChar, 1);

        private static readonly string s_thisDirectory = "." + s_directorySeparator;

        private static readonly char[] s_wildcardCharacters = { '*', '?' };
        private static readonly char[] s_wildcardAndSemicolonCharacters = { '*', '?', ';' };

        // on OSX both System.IO.Path separators are '/', so we have to use the literals
        internal static readonly char[] directorySeparatorCharacters = { '/', '\\' };
        internal static readonly string[] directorySeparatorStrings = directorySeparatorCharacters.Select(c => c.ToString()).ToArray();

        internal static readonly GetFileSystemEntries s_defaultGetFileSystemEntries = new GetFileSystemEntries(GetAccessibleFileSystemEntries);
        private static readonly DirectoryExists s_defaultDirectoryExists = new DirectoryExists(Directory.Exists);

        private static readonly Lazy<ConcurrentDictionary<string, string[]>> s_cachedFileEnumerations = new Lazy<ConcurrentDictionary<string, string[]>>(() => new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
        private static readonly Lazy<ConcurrentDictionary<string, object>> s_cachedFileEnumerationsLock = new Lazy<ConcurrentDictionary<string, object>>(() => new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Cache of the list of invalid path characters, because this method returns a clone (for security reasons)
        /// which can cause significant transient allocations
        /// </summary>
        private static readonly char[] s_invalidPathChars = Path.GetInvalidPathChars();

        internal const RegexOptions DefaultRegexOptions = RegexOptions.IgnoreCase;

        /// <summary>
        /// The type of entity that GetFileSystemEntries should return.
        /// </summary>
        internal enum FileSystemEntity
        {
            Files,
            Directories,
            FilesAndDirectories
        };

        /// <summary>
        /// Delegate defines the GetFileSystemEntries signature that GetLongPathName uses
        /// to enumerate directories on the file system.
        /// </summary>
        /// <param name="entityType">Files, Directories, or Files and Directories</param>
        /// <param name="path">The path to search.</param>
        /// <param name="pattern">The file pattern.</param>
        /// <param name="projectDirectory"></param>
        /// <param name="stripProjectDirectory"></param>
        /// <returns>An enumerable of filesystem entries.</returns>
        internal delegate IEnumerable<string> GetFileSystemEntries(FileSystemEntity entityType, string path, string pattern, string projectDirectory, bool stripProjectDirectory);

        internal static void TestClearCaches()
        {
            s_cachedFileEnumerations.Value.Clear();
            s_cachedFileEnumerationsLock.Value.Clear();
        }

        /// <summary>
        /// Determines whether the given path has any wild card characters.
        /// </summary>
        /// <param name="filespec"></param>
        /// <returns></returns>
        internal static bool HasWildcards(string filespec)
        {
            return -1 != filespec.IndexOfAny(s_wildcardCharacters);
        }

        /// <summary>
        /// Determines whether the given path has any wild card characters or any semicolons.
        /// </summary>
        internal static bool HasWildcardsSemicolonItemOrPropertyReferences(string filespec)
        {
            return
                (
                (-1 != filespec.IndexOfAny(s_wildcardAndSemicolonCharacters)) ||
                filespec.Contains("$(") ||
                filespec.Contains("@(")
                );
        }

        /// <summary>
        /// Get the files and\or folders specified by the given path and pattern.
        /// </summary>
        /// <param name="entityType">Whether Files, Directories or both.</param>
        /// <param name="path">The path to search.</param>
        /// <param name="pattern">The pattern to search.</param>
        /// <param name="projectDirectory">The directory for the project within which the call is made</param>
        /// <param name="stripProjectDirectory">If true the project directory should be stripped</param>
        /// <returns></returns>
        private static IEnumerable<string> GetAccessibleFileSystemEntries(FileSystemEntity entityType, string path, string pattern, string projectDirectory, bool stripProjectDirectory)
        {
            path = FileUtilities.FixFilePath(path);
            IEnumerable<string> files = null;
            switch (entityType)
            {
                case FileSystemEntity.Files: files = GetAccessibleFiles(path, pattern, projectDirectory, stripProjectDirectory); break;
                case FileSystemEntity.Directories: files = GetAccessibleDirectories(path, pattern); break;
                case FileSystemEntity.FilesAndDirectories: files = GetAccessibleFilesAndDirectories(path, pattern); break;
                default:
                    ErrorUtilities.VerifyThrow(false, "Unexpected filesystem entity type.");
                    break;
            }

            return files;
        }

        /// <summary>
        /// Returns an array of file system entries matching the specified search criteria. Inaccessible or non-existent file
        /// system entries are skipped.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="pattern"></param>
        /// <returns>An enumerable of matching file system entries (can be empty).</returns>
        private static IEnumerable<string> GetAccessibleFilesAndDirectories(string path, string pattern)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    return ShouldEnforceMatching(pattern)
                        ? Directory.EnumerateFileSystemEntries(path, pattern).Where(o => IsMatch(Path.GetFileName(o), pattern))
                        : Directory.EnumerateFileSystemEntries(path, pattern);
                }
                // for OS security
                catch (UnauthorizedAccessException)
                {
                    // do nothing
                }
                // for code access security
                catch (System.Security.SecurityException)
                {
                    // do nothing
                }
            }
            return Enumerable.Empty<string>();
        }

        /// <summary>
        /// Determine if the given search pattern will behave differently on Windows and Unix
        /// </summary>
        /// <param name="searchPattern">The search pattern to check</param>
        /// <returns></returns>
        private static bool ShouldEnforceMatching(string searchPattern)
        {
            // NOTE: Windows matches loosely in three cases (in the absence of the * wildcard in the extension):
            // 1) if the extension ends with the ? wildcard, it matches files with shorter extensions also e.g. "file.tx?" would
            //    match both "file.txt" and "file.tx"
            // 2) if the extension is three characters, and the filename contains the * wildcard, it matches files with longer
            //    extensions that start with the same three characters e.g. "*.htm" would match both "file.htm" and "file.html"
            // 3) if the ? wildcard is to the left of a period, it matches files with shorter name e.g. ???.txt would match
            //    foo.txt, fo.txt and also f.txt
            string extensionPart = Path.GetExtension(searchPattern);
            return searchPattern.IndexOf("?.", StringComparison.Ordinal) != -1 ||
                   (
                       extensionPart != null &&
                       extensionPart.Length == (3 + 1 /* +1 for the period */) &&
                       searchPattern.IndexOf('*') != -1
                   ) ||
                   searchPattern.EndsWith("?", StringComparison.Ordinal);
        }

        /// <summary>
        /// Same as Directory.GetFiles(...) except that files that
        /// aren't accessible are skipped instead of throwing an exception.
        /// 
        /// Other exceptions are passed through.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="filespec">The pattern.</param>
        /// <param name="projectDirectory">The project directory</param>
        /// <param name="stripProjectDirectory"></param>
        /// <returns>Files that can be accessed.</returns>
        private static IEnumerable<string> GetAccessibleFiles
        (
            string path,
            string filespec,     // can be null
            string projectDirectory,
            bool stripProjectDirectory
        )
        {
            try
            {
                // look in current directory if no path specified
                string dir = ((path.Length == 0) ? s_thisDirectory : path);

                // get all files in specified directory, unless a file-spec has been provided
                IEnumerable<string> files;
                if (filespec == null)
                {
                    files = Directory.EnumerateFiles(dir);
                }
                else
                {
                    files = Directory.EnumerateFiles(dir, filespec);
                    if (ShouldEnforceMatching(filespec))
                    {
                        files = files.Where(o => IsMatch(Path.GetFileName(o), filespec));
                    }
                }
                // If the Item is based on a relative path we need to strip
                // the current directory from the front
                if (stripProjectDirectory)
                {
                    return RemoveProjectDirectory(files, projectDirectory);
                }
                // Files in the current directory are coming back with a ".\"
                // prepended to them.  We need to remove this; it breaks the
                // IDE, which expects just the filename if it is in the current
                // directory.  But only do this if the original path requested
                // didn't itself contain a ".\".
                else if (!path.StartsWith(s_thisDirectory, StringComparison.Ordinal))
                {
                    return RemoveInitialDotSlash(files);
                }

                return files;
            }
            catch (System.Security.SecurityException)
            {
                // For code access security.
                return Array.Empty<string>();
            }
            catch (System.UnauthorizedAccessException)
            {
                // For OS security.
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Same as Directory.GetDirectories(...) except that files that
        /// aren't accessible are skipped instead of throwing an exception.
        /// 
        /// Other exceptions are passed through.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="pattern">Pattern to match</param>
        /// <returns>Accessible directories.</returns>
        private static IEnumerable<string> GetAccessibleDirectories
        (
            string path,
            string pattern
        )
        {
            try
            {
                IEnumerable<string> directories = null;

                if (pattern == null)
                {
                    directories = Directory.EnumerateDirectories((path.Length == 0) ? s_thisDirectory : path);
                }
                else
                {
                    directories = Directory.EnumerateDirectories((path.Length == 0) ? s_thisDirectory : path, pattern);
                    if (ShouldEnforceMatching(pattern))
                    {
                        directories = directories.Where(o => IsMatch(Path.GetDirectoryName(o), pattern));
                    }
                }

                // Subdirectories in the current directory are coming back with a ".\"
                // prepended to them.  We need to remove this; it breaks the
                // IDE, which expects just the filename if it is in the current
                // directory.  But only do this if the original path requested
                // didn't itself contain a ".\".
                if (!path.StartsWith(s_thisDirectory, StringComparison.Ordinal))
                {
                    return RemoveInitialDotSlash(directories);
                }

                return directories;
            }
            catch (System.Security.SecurityException)
            {
                // For code access security.
                return Enumerable.Empty<string>();
            }
            catch (System.UnauthorizedAccessException)
            {
                // For OS security.
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Given a path name, get its long version.
        /// </summary>
        /// <param name="path">The short path.</param>
        /// <returns>The long path.</returns>
        internal static string GetLongPathName
        (
            string path
        )
        {
            return GetLongPathName(path, s_defaultGetFileSystemEntries);
        }

        /// <summary>
        /// Given a path name, get its long version.
        /// </summary>
        /// <param name="path">The short path.</param>
        /// <param name="getFileSystemEntries">Delegate.</param>
        /// <returns>The long path.</returns>
        internal static string GetLongPathName
        (
            string path,
            GetFileSystemEntries getFileSystemEntries
        )
        {
            if (path.IndexOf("~", StringComparison.Ordinal) == -1)
            {
                // A path with no '~' must not be a short name.
                return path;
            }

            ErrorUtilities.VerifyThrow(!HasWildcards(path),
                "GetLongPathName does not handle wildcards and was passed '{0}'.", path);

            string[] parts = path.Split(directorySeparatorCharacters);
            string pathRoot;
            int startingElement = 0;

            bool isUnc = path.StartsWith(s_directorySeparator + s_directorySeparator, StringComparison.Ordinal);
            if (isUnc)
            {
                pathRoot = s_directorySeparator + s_directorySeparator;
                pathRoot += parts[2];
                pathRoot += s_directorySeparator;
                pathRoot += parts[3];
                pathRoot += s_directorySeparator;
                startingElement = 4;
            }
            else
            {
                // Is it relative?
                if (path.Length > 2 && path[1] == ':')
                {
                    // Not relative
                    pathRoot = parts[0] + s_directorySeparator;
                    startingElement = 1;
                }
                else
                {
                    // Relative
                    pathRoot = String.Empty;
                    startingElement = 0;
                }
            }

            // Build up an array of parts. These elements may be "" if there are
            // extra slashes.
            string[] longParts = new string[parts.Length - startingElement];

            string longPath = pathRoot;
            for (int i = startingElement; i < parts.Length; ++i)
            {
                // If there is a zero-length part, then that means there was an extra slash.
                if (parts[i].Length == 0)
                {
                    longParts[i - startingElement] = String.Empty;
                }
                else
                {
                    if (parts[i].IndexOf("~", StringComparison.Ordinal) == -1)
                    {
                        // If there's no ~, don't hit the disk.
                        longParts[i - startingElement] = parts[i];
                        longPath = Path.Combine(longPath, parts[i]);
                    }
                    else
                    {
                        // getFileSystemEntries(...) returns an empty array if longPath doesn't exist.
                        string[] entries = getFileSystemEntries(FileSystemEntity.FilesAndDirectories, longPath, parts[i], null, false).Take(2).ToArray();

                        if (0 == entries.Length)
                        {
                            // The next part doesn't exist. Therefore, no more of the path will exist.
                            // Just return the rest.
                            for (int j = i; j < parts.Length; ++j)
                            {
                                longParts[j - startingElement] = parts[j];
                            }
                            break;
                        }

                        // Since we know there are no wild cards, this should be length one.
                        ErrorUtilities.VerifyThrow(entries.Length == 1,
                            "Unexpected number of entries (more than 1) found when enumerating '{0}' under '{1}'. Original path was '{2}'",
                            parts[i], longPath, path, entries.Length);

                        // Entries[0] contains the full path.
                        longPath = entries[0];

                        // We just want the trailing node.
                        longParts[i - startingElement] = Path.GetFileName(longPath);
                    }
                }
            }

            return pathRoot + String.Join(s_directorySeparator, longParts);
        }

        /// <summary>
        /// Given a filespec, split it into left-most 'fixed' dir part, middle 'wildcard' dir part, and filename part.
        /// The filename part may have wildcard characters in it.
        /// </summary>
        /// <param name="filespec">The filespec to be decomposed.</param>
        /// <param name="fixedDirectoryPart">Receives the fixed directory part.</param>
        /// <param name="wildcardDirectoryPart">The wildcard directory part.</param>
        /// <param name="filenamePart">The filename part.</param>
        /// <param name="getFileSystemEntries">Delegate.</param>
        internal static void SplitFileSpec
        (
            string filespec,
            out string fixedDirectoryPart,
            out string wildcardDirectoryPart,
            out string filenamePart,
            GetFileSystemEntries getFileSystemEntries
        )
        {
            PreprocessFileSpecForSplitting
            (
                filespec,
                out fixedDirectoryPart,
                out wildcardDirectoryPart,
                out filenamePart
            );

            /* 
             * Handle the special case in which filenamePart is '**'.
             * In this case, filenamePart becomes '*.*' and the '**' is appended
             * to the end of the wildcardDirectory part.
             * This is so that later regular expression matching can accurately
             * pull out the different parts (fixed, wildcard, filename) of given
             * file specs.
             */
            if (recursiveDirectoryMatch == filenamePart)
            {
                wildcardDirectoryPart += recursiveDirectoryMatch;
                wildcardDirectoryPart += s_directorySeparator;
                filenamePart = "*.*";
            }

            fixedDirectoryPart = FileMatcher.GetLongPathName(fixedDirectoryPart, getFileSystemEntries);
        }

        /// <summary>
        /// Do most of the grunt work of splitting the filespec into parts.
        /// Does not handle post-processing common to the different matching
        /// paths.
        /// </summary>
        /// <param name="filespec">The filespec to be decomposed.</param>
        /// <param name="fixedDirectoryPart">Receives the fixed directory part.</param>
        /// <param name="wildcardDirectoryPart">The wildcard directory part.</param>
        /// <param name="filenamePart">The filename part.</param>
        private static void PreprocessFileSpecForSplitting
        (
            string filespec,
            out string fixedDirectoryPart,
            out string wildcardDirectoryPart,
            out string filenamePart
        )
        {
            filespec = FileUtilities.FixFilePath(filespec);
            int indexOfLastDirectorySeparator = filespec.LastIndexOfAny(directorySeparatorCharacters);
            if (-1 == indexOfLastDirectorySeparator)
            {
                /*
                 * No dir separator found. This is either this form,
                 * 
                 *      Source.cs
                 *      *.cs
                 * 
                 *  or this form,
                 * 
                 *     **
                 */
                fixedDirectoryPart = String.Empty;
                wildcardDirectoryPart = String.Empty;
                filenamePart = filespec;
                return;
            }

            int indexOfFirstWildcard = filespec.IndexOfAny(s_wildcardCharacters);
            if
            (
                -1 == indexOfFirstWildcard
                || indexOfFirstWildcard > indexOfLastDirectorySeparator
            )
            {
                /*
                 * There is at least one dir separator, but either there is no wild card or the
                 * wildcard is after the dir separator.
                 *
                 * The form is one of these:
                 * 
                 *      dir1\Source.cs
                 *      dir1\*.cs
                 * 
                 * Where the trailing spec is meant to be a filename. Or,
                 * 
                 *      dir1\**
                 * 
                 * Where the trailing spec is meant to be any file recursively.
                 */

                // We know the fixed director part now.
                fixedDirectoryPart = filespec.Substring(0, indexOfLastDirectorySeparator + 1);
                wildcardDirectoryPart = String.Empty;
                filenamePart = filespec.Substring(indexOfLastDirectorySeparator + 1);
                return;
            }

            /*
             * Find the separator right before the first wildcard.
             */
            string filespecLeftOfWildcard = filespec.Substring(0, indexOfFirstWildcard);
            int indexOfSeparatorBeforeWildCard = filespecLeftOfWildcard.LastIndexOfAny(directorySeparatorCharacters);
            if (-1 == indexOfSeparatorBeforeWildCard)
            {
                /*
                 * There is no separator before the wildcard, so the form is like this:
                 * 
                 *      dir?\Source.cs
                 * 
                 * or this,
                 * 
                 *      dir?\**
                 */
                fixedDirectoryPart = String.Empty;
                wildcardDirectoryPart = filespec.Substring(0, indexOfLastDirectorySeparator + 1);
                filenamePart = filespec.Substring(indexOfLastDirectorySeparator + 1);
                return;
            }

            /*
             * There is at least one wildcard and one dir separator, split parts out.
             */
            fixedDirectoryPart = filespec.Substring(0, indexOfSeparatorBeforeWildCard + 1);
            wildcardDirectoryPart = filespec.Substring(indexOfSeparatorBeforeWildCard + 1, indexOfLastDirectorySeparator - indexOfSeparatorBeforeWildCard);
            filenamePart = filespec.Substring(indexOfLastDirectorySeparator + 1);
        }

        /// <summary>
        /// Removes the leading ".\" from all of the paths in the array. 
        /// </summary>
        /// <param name="paths">Paths to remove .\ from.</param>
        private static IEnumerable<string> RemoveInitialDotSlash
        (
            IEnumerable<string> paths
        )
        {
            foreach (string path in paths)
            {
                if (path.StartsWith(s_thisDirectory, StringComparison.Ordinal))
                {
                    yield return path.Substring(2);
                }
                else
                {
                    yield return path;
                }
            }
        }


        /// <summary>
        /// Checks if the char is a DirectorySeparatorChar or a AltDirectorySeparatorChar
        /// </summary>
        /// <param name="c"></param>
        /// <returns></returns>
        internal static bool IsDirectorySeparator(char c)
        {
            return (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
        }
        /// <summary>
        /// Removes the current directory converting the file back to relative path 
        /// </summary>
        /// <param name="paths">Paths to remove current directory from.</param>
        /// <param name="projectDirectory"></param>
        internal static IEnumerable<string> RemoveProjectDirectory
        (
            IEnumerable<string> paths,
            string projectDirectory
        )
        {
            bool directoryLastCharIsSeparator = IsDirectorySeparator(projectDirectory[projectDirectory.Length - 1]);
            foreach (string path in paths)
            {
                if (path.StartsWith(projectDirectory, StringComparison.Ordinal))
                {
                    // If the project directory did not end in a slash we need to check to see if the next char in the path is a slash
                    if (!directoryLastCharIsSeparator)
                    {
                        //If the next char after the project directory is not a slash, skip this path
                        if (path.Length <= projectDirectory.Length ||
                            !IsDirectorySeparator(path[projectDirectory.Length]))
                        {
                            yield return path;
                            continue;
                        }
                        yield return path.Substring(projectDirectory.Length + 1);
                    }
                    else
                    {
                        yield return path.Substring(projectDirectory.Length);
                    }
                }
                else
                {
                    yield return path;
                }
            }
        }

        struct RecursiveStepResult
        {
            public string RemainingWildcardDirectory;
            public bool ConsiderFiles;
            public bool NeedToProcessEachFile;
            public string DirectoryPattern;
            public bool NeedDirectoryRecursion;
        }

        class FilesSearchData
        {
            public FilesSearchData(
                string filespec,                // can be null
                Regex regexFileMatch,           // can be null
                bool needsRecursion
                )
            {
                Filespec = filespec;
                RegexFileMatch = regexFileMatch;
                NeedsRecursion = needsRecursion;
            }

            /// <summary>
            /// The filespec.
            /// </summary>
            public string Filespec { get; }
            /// <summary>
            /// Wild-card matching.
            /// </summary>
            public Regex RegexFileMatch { get; }
            /// <summary>
            /// If true, then recursion is required.
            /// </summary>
            public bool NeedsRecursion { get; }
        }

        struct RecursionState
        {
            /// <summary>
            /// The directory to search in
            /// </summary>
            public string BaseDirectory;
            /// <summary>
            /// The remaining, wildcard part of the directory.
            /// </summary>
            public string RemainingWildcardDirectory;
            /// <summary>
            /// Data about a search that does not change as the search recursively traverses directories
            /// </summary>
            public FilesSearchData SearchData;
        }

        /// <summary>
        /// Get all files that match either the file-spec or the regular expression. 
        /// </summary>
        /// <param name="listOfFiles">List of files that gets populated.</param>
        /// <param name="recursionState">Information about the search</param>
        /// <param name="projectDirectory"></param>
        /// <param name="stripProjectDirectory"></param>
        /// <param name="getFileSystemEntries">Delegate.</param>
        /// <param name="searchesToExclude">Patterns to exclude from the results</param>
        /// <param name="searchesToExcludeInSubdirs">exclude patterns that might activate farther down the directory tree. Keys assume paths are normalized with forward slashes and no trailing slashes</param>
        private static void GetFilesRecursive
        (
            IList<string> listOfFiles,
            RecursionState recursionState,
            string projectDirectory,
            bool stripProjectDirectory,
            GetFileSystemEntries getFileSystemEntries,
            IList<RecursionState> searchesToExclude,
            Dictionary<string, List<RecursionState>> searchesToExcludeInSubdirs
        )
        {
            ErrorUtilities.VerifyThrow((recursionState.SearchData.Filespec== null) || (recursionState.SearchData.RegexFileMatch == null),
                "File-spec overrides the regular expression -- pass null for file-spec if you want to use the regular expression.");

            ErrorUtilities.VerifyThrow((recursionState.SearchData.Filespec != null) || (recursionState.SearchData.RegexFileMatch != null),
                "Need either a file-spec or a regular expression to match files.");

            ErrorUtilities.VerifyThrow(recursionState.RemainingWildcardDirectory != null, "Expected non-null remaning wildcard directory.");

            RecursiveStepResult[] excludeNextSteps = null;
            //  Determine if any of searchesToExclude is necessarily a superset of the results that will be returned.
            //  This means all results will be excluded and we should bail out now.
            if (searchesToExclude != null)
            {
                excludeNextSteps = new RecursiveStepResult[searchesToExclude.Count];
                for (int i = 0; i < searchesToExclude.Count; i++)
                {
                    RecursionState searchToExclude = searchesToExclude[i];
                    //  The BaseDirectory of all the exclude searches should be the same as the include one
                    Debug.Assert(FileUtilities.PathsEqual(searchToExclude.BaseDirectory, recursionState.BaseDirectory), "Expected exclude search base directory to match include search base directory");

                    excludeNextSteps[i] = GetFilesRecursiveStep(searchesToExclude[i]);

                    //  We can exclude all results in this folder if:
                    if (
                        //  We are matching files based on a filespec and not a regular expression
                        searchToExclude.SearchData.Filespec != null &&
                        //  The wildcard path portion of the excluded search matches the include search
                        FileUtilities.PathsEqual(searchToExclude.RemainingWildcardDirectory, recursionState.RemainingWildcardDirectory) &&
                        //  The exclude search will match ALL filenames OR
                        (searchToExclude.SearchData.Filespec == "*" || searchToExclude.SearchData.Filespec == "*.*" ||
                            //  The exclude search filename pattern matches the include search's pattern
                            searchToExclude.SearchData.Filespec == recursionState.SearchData.Filespec))
                    {
                        //  We won't get any results from this search that we would end up keeping
                        return;
                    }
                }
            }

            RecursiveStepResult nextStep = GetFilesRecursiveStep(recursionState);

            foreach (string file in GetFilesForStep(nextStep, recursionState, projectDirectory,
                stripProjectDirectory, getFileSystemEntries))
            {
                if (excludeNextSteps == null)
                {
                    listOfFiles.Add(file);
                    continue;
                }
                var exclude = false;
                for (int i = 0; i < excludeNextSteps.Length; i++)
                {
                    RecursiveStepResult excludeNextStep = excludeNextSteps[i];
                    if (excludeNextStep.ConsiderFiles && MatchFileRecursionStep(searchesToExclude[i], file))
                    {
                        exclude = true;
                        break;
                    }
                }
                if (!exclude)
                {
                    listOfFiles.Add(file);
                }
            }

            if (!nextStep.NeedDirectoryRecursion)
            {
                return;
            }

            foreach (string subdir in getFileSystemEntries(FileSystemEntity.Directories, recursionState.BaseDirectory, nextStep.DirectoryPattern, null, false))
            {
                //  RecursionState is a struct so this copies it
                var newRecursionState = recursionState;

                newRecursionState.BaseDirectory = subdir;
                newRecursionState.RemainingWildcardDirectory = nextStep.RemainingWildcardDirectory;

                List<RecursionState> newSearchesToExclude = null;

                if (excludeNextSteps != null)
                {
                    newSearchesToExclude = new List<RecursionState>();

                    for (int i = 0; i < excludeNextSteps.Length; i++)
                    {
                        if (excludeNextSteps[i].NeedDirectoryRecursion &&
                            (excludeNextSteps[i].DirectoryPattern == null || IsMatch(Path.GetDirectoryName(subdir), excludeNextSteps[i].DirectoryPattern)))
                        {
                            RecursionState thisExcludeStep = searchesToExclude[i];
                            thisExcludeStep.BaseDirectory = subdir;
                            thisExcludeStep.RemainingWildcardDirectory = excludeNextSteps[i].RemainingWildcardDirectory;
                            newSearchesToExclude.Add(thisExcludeStep);
                        }
                    }
                }

                if (searchesToExcludeInSubdirs != null)
                {
                    List<RecursionState> searchesForSubdir;

                    // The normalization fixes https://github.com/Microsoft/msbuild/issues/917
                    // and is a partial fix for https://github.com/Microsoft/msbuild/issues/724
                    if (searchesToExcludeInSubdirs.TryGetValue(subdir.NormalizeForPathComparison(), out searchesForSubdir))
                    {
                        //  We've found the base directory that these exclusions apply to.  So now add them as normal searches
                        if (newSearchesToExclude == null)
                        {
                            newSearchesToExclude = new List<RecursionState>();
                        }
                        newSearchesToExclude.AddRange(searchesForSubdir);
                    }
                }

                // We never want to strip the project directory from the leaves, because the current 
                // process directory maybe different
                GetFilesRecursive(
                    listOfFiles,
                    newRecursionState,
                    projectDirectory,
                    stripProjectDirectory,
                    getFileSystemEntries,
                    newSearchesToExclude,
                    searchesToExcludeInSubdirs);
            }
        }

        private static IEnumerable<string> GetFilesForStep
        (
            RecursiveStepResult stepResult,
            RecursionState recursionState,
            string projectDirectory,
            bool stripProjectDirectory,
            GetFileSystemEntries getFileSystemEntries
        )
        {
            if (!stepResult.ConsiderFiles)
            {
                return Enumerable.Empty<string>();
            }
            IEnumerable<string> files = getFileSystemEntries(FileSystemEntity.Files, recursionState.BaseDirectory,
                recursionState.SearchData.Filespec, projectDirectory, stripProjectDirectory);

            if (!stepResult.NeedToProcessEachFile)
            {
                return files;
            }
            return files.Where(o => MatchFileRecursionStep(recursionState, o));
        }

        private static bool MatchFileRecursionStep(RecursionState recursionState, string file)
        {
            if (recursionState.SearchData.Filespec != null)
            {
                return IsMatch(Path.GetFileName(file), recursionState.SearchData.Filespec);
            }

            // if no file-spec provided, match the file to the regular expression
            // PERF NOTE: Regex.IsMatch() is an expensive operation, so we avoid it whenever possible
            return recursionState.SearchData.RegexFileMatch.IsMatch(file);
        }

        private static RecursiveStepResult GetFilesRecursiveStep
        (
            RecursionState recursionState
        )
        {
            RecursiveStepResult ret = new RecursiveStepResult();

            /*
             * Get the matching files.
             */
            bool considerFiles = false;

            // Only consider files if...
            if (recursionState.RemainingWildcardDirectory.Length == 0)
            {
                // We've reached the end of the wildcard directory elements.
                considerFiles = true;
            }
            else if (recursionState.RemainingWildcardDirectory.IndexOf(recursiveDirectoryMatch, StringComparison.Ordinal) == 0)
            {
                // or, we've reached a "**" so everything else is matched recursively.
                considerFiles = true;
            }
            ret.ConsiderFiles = considerFiles;
            if (considerFiles)
            {
                ret.NeedToProcessEachFile = recursionState.SearchData.Filespec == null;
            }

            /*
             * Recurse into subdirectories.
             */
            if (recursionState.SearchData.NeedsRecursion && recursionState.RemainingWildcardDirectory.Length > 0)
            {
                // Find the next directory piece.
                string pattern = null;

                if (!IsRecursiveDirectoryMatch(recursionState.RemainingWildcardDirectory))
                {
                    int indexOfNextSlash = recursionState.RemainingWildcardDirectory.IndexOfAny(directorySeparatorCharacters);
                    ErrorUtilities.VerifyThrow(indexOfNextSlash != -1, "Slash should be guaranteed.");

                    pattern = recursionState.RemainingWildcardDirectory.Substring(0, indexOfNextSlash);

                    if (pattern == recursiveDirectoryMatch)
                    {
                        // If pattern turned into **, then there's no choice but to enumerate everything.
                        pattern = null;
                        recursionState.RemainingWildcardDirectory = recursiveDirectoryMatch;
                    }
                    else
                    {
                        // Peel off the leftmost directory piece. So for example, if remainingWildcardDirectory
                        // contains:
                        //
                        //        ?emp\foo\**\bar
                        //
                        // then put '?emp' into pattern. Then put the remaining part,
                        //
                        //        foo\**\bar
                        //
                        // back into remainingWildcardDirectory.
                        // This is a performance optimization. We don't want to enumerate everything if we 
                        // don't have to.
                        recursionState.RemainingWildcardDirectory = recursionState.RemainingWildcardDirectory.Substring(indexOfNextSlash + 1);
                    }
                }

                ret.NeedDirectoryRecursion = true;
                ret.RemainingWildcardDirectory = recursionState.RemainingWildcardDirectory;
                ret.DirectoryPattern = pattern;
            }

            return ret;
        }

        /// <summary>
        /// Given a file spec, create a regular expression that will match that
        /// file spec.
        /// 
        /// PERF WARNING: this method is called in performance-critical
        /// scenarios, so keep it fast and cheap
        /// </summary>
        /// <param name="fixedDirectoryPart">The fixed directory part.</param>
        /// <param name="wildcardDirectoryPart">The wildcard directory part.</param>
        /// <param name="filenamePart">The filename part.</param>
        /// <param name="isLegalFileSpec">Receives whether this pattern is legal or not.</param>
        /// <returns>The regular expression string.</returns>
        private static string RegularExpressionFromFileSpec
        (
            string fixedDirectoryPart,
            string wildcardDirectoryPart,
            string filenamePart,
            out bool isLegalFileSpec
        )
        {
            isLegalFileSpec = true;

            /*
             * The code below uses tags in the form <:tag:> to encode special information
             * while building the regular expression.
             * 
             * This format was chosen because it's not a legal form for filespecs. If the
             * filespec comes in with either "<:" or ":>", return isLegalFileSpec=false to
             * prevent intrusion into the special processing.
             */
            if ((fixedDirectoryPart.IndexOf("<:", StringComparison.Ordinal) != -1) ||
                (fixedDirectoryPart.IndexOf(":>", StringComparison.Ordinal) != -1) ||
                (wildcardDirectoryPart.IndexOf("<:", StringComparison.Ordinal) != -1) ||
                (wildcardDirectoryPart.IndexOf(":>", StringComparison.Ordinal) != -1) ||
                (filenamePart.IndexOf("<:", StringComparison.Ordinal) != -1) ||
                (filenamePart.IndexOf(":>", StringComparison.Ordinal) != -1))
            {
                isLegalFileSpec = false;
                return String.Empty;
            }

            /*
             * Its not legal for there to be a ".." after a wildcard.
             */
            if (wildcardDirectoryPart.Contains(dotdot))
            {
                isLegalFileSpec = false;
                return String.Empty;
            }

            /* 
             * Trailing dots in file names have to be treated specially.
             * We want:
             * 
             *     *. to match foo
             * 
             * but 'foo' doesn't have a trailing '.' so we need to handle this while still being careful 
             * not to match 'foo.txt'
             */
            if (filenamePart.EndsWith(".", StringComparison.Ordinal))
            {
                filenamePart = filenamePart.Replace("*", "<:anythingbutdot:>");
                filenamePart = filenamePart.Replace("?", "<:anysinglecharacterbutdot:>");
                filenamePart = filenamePart.Substring(0, filenamePart.Length - 1);
            }

            /*
             * Now, build up the starting filespec but put tags in to identify where the fixedDirectory,
             * wildcardDirectory and filenamePart are. Also tag the beginning of the line and the end of
             * the line, so that we can identify patterns by whether they're on one end or the other.
             */
            StringBuilder matchFileExpression = new StringBuilder();
            matchFileExpression.Append("<:bol:>");
            matchFileExpression.Append("<:fixeddir:>").Append(fixedDirectoryPart).Append("<:endfixeddir:>");
            matchFileExpression.Append("<:wildcarddir:>").Append(wildcardDirectoryPart).Append("<:endwildcarddir:>");
            matchFileExpression.Append("<:filename:>").Append(filenamePart).Append("<:endfilename:>");
            matchFileExpression.Append("<:eol:>");

            /*
             *  Call out our special matching characters.
             */
            foreach (var separator in directorySeparatorStrings)
            {
                matchFileExpression.Replace(separator, "<:dirseparator:>");
            }

            /*
             * Capture the leading \\ in UNC paths, so that the doubled slash isn't
             * reduced in a later step.
             */
            matchFileExpression.Replace("<:fixeddir:><:dirseparator:><:dirseparator:>", "<:fixeddir:><:uncslashslash:>");

            /*
             * Iteratively reduce four cases involving directory separators
             * 
             *  (1) <:dirseparator:>.<:dirseparator:> -> <:dirseparator:>
             *        This is an identity, so for example, these two are equivalent,
             * 
             *            dir1\.\dir2 == dir1\dir2
             * 
             *    (2) <:dirseparator:><:dirseparator:> -> <:dirseparator:>
             *      Double directory separators are treated as a single directory separator,
             *      so, for example, this is an identity:
             * 
             *          f:\dir1\\dir2 == f:\dir1\dir2
             * 
             *      The single exemption is for UNC path names, like this:
             * 
             *          \\server\share != \server\share
             * 
             *      This case is handled by the <:uncslashslash:> which was substituted in
             *      a prior step.
             * 
             *  (3) <:fixeddir:>.<:dirseparator:>.<:dirseparator:> -> <:fixeddir:>.<:dirseparator:>
             *      A ".\" at the beginning of a line is equivalent to nothing, so:
             * 
             *          .\.\dir1\file.txt == .\dir1\file.txt
             * 
             *  (4) <:dirseparator:>.<:eol:> -> <:eol:>
             *      A "\." at the end of a line is equivalent to nothing, so:
             * 
             *          dir1\dir2\. == dir1\dir2             *
             */
            int sizeBefore;
            do
            {
                sizeBefore = matchFileExpression.Length;

                // NOTE: all these replacements will necessarily reduce the expression length i.e. length will either reduce or
                // stay the same through this loop
                matchFileExpression.Replace("<:dirseparator:>.<:dirseparator:>", "<:dirseparator:>");
                matchFileExpression.Replace("<:dirseparator:><:dirseparator:>", "<:dirseparator:>");
                matchFileExpression.Replace("<:fixeddir:>.<:dirseparator:>.<:dirseparator:>", "<:fixeddir:>.<:dirseparator:>");
                matchFileExpression.Replace("<:dirseparator:>.<:endfilename:>", "<:endfilename:>");
                matchFileExpression.Replace("<:filename:>.<:endfilename:>", "<:filename:><:endfilename:>");

                ErrorUtilities.VerifyThrow(matchFileExpression.Length <= sizeBefore,
                    "Expression reductions cannot increase the length of the expression.");
            } while (matchFileExpression.Length < sizeBefore);

            /*
             * Collapse **\** into **.
             */
            do
            {
                sizeBefore = matchFileExpression.Length;
                matchFileExpression.Replace(recursiveDirectoryMatch + "<:dirseparator:>" + recursiveDirectoryMatch, recursiveDirectoryMatch);

                ErrorUtilities.VerifyThrow(matchFileExpression.Length <= sizeBefore,
                    "Expression reductions cannot increase the length of the expression.");
            } while (matchFileExpression.Length < sizeBefore);

            /*
             * Call out legal recursion operators:
             * 
             *        fixed-directory + **\
             *        \**\
             *        **\**
             * 
             */
            do
            {
                sizeBefore = matchFileExpression.Length;
                matchFileExpression.Replace("<:dirseparator:>" + recursiveDirectoryMatch + "<:dirseparator:>", "<:middledirs:>");
                matchFileExpression.Replace("<:wildcarddir:>" + recursiveDirectoryMatch + "<:dirseparator:>", "<:wildcarddir:><:leftdirs:>");

                ErrorUtilities.VerifyThrow(matchFileExpression.Length <= sizeBefore,
                    "Expression reductions cannot increase the length of the expression.");
            } while (matchFileExpression.Length < sizeBefore);


            /*
             * By definition, "**" must appear alone between directory slashes. If there is any remaining "**" then this is not
             * a valid filespec.
             */
            // NOTE: this condition is evaluated left-to-right -- this is important because we want the length BEFORE stripping
            // any "**"s remaining in the expression
            if (matchFileExpression.Length > matchFileExpression.Replace(recursiveDirectoryMatch, null).Length)
            {
                isLegalFileSpec = false;
                return String.Empty;
            }

            /*
             * Remaining call-outs not involving "**"
             */
            matchFileExpression.Replace("*.*", "<:anynonseparator:>");
            matchFileExpression.Replace("*", "<:anynonseparator:>");
            matchFileExpression.Replace("?", "<:singlecharacter:>");

            /*
             *  Escape all special characters defined for regular expresssions.
             */
            matchFileExpression.Replace("\\", "\\\\"); // Must be first.
            matchFileExpression.Replace("$", "\\$");
            matchFileExpression.Replace("(", "\\(");
            matchFileExpression.Replace(")", "\\)");
            matchFileExpression.Replace("*", "\\*");
            matchFileExpression.Replace("+", "\\+");
            matchFileExpression.Replace(".", "\\.");
            matchFileExpression.Replace("[", "\\[");
            matchFileExpression.Replace("?", "\\?");
            matchFileExpression.Replace("^", "\\^");
            matchFileExpression.Replace("{", "\\{");
            matchFileExpression.Replace("|", "\\|");

            /*
             *  Now, replace call-outs with their regex equivalents.
             */
            matchFileExpression.Replace("<:middledirs:>", "((/)|(\\\\)|(/.*/)|(/.*\\\\)|(\\\\.*\\\\)|(\\\\.*/))");
            matchFileExpression.Replace("<:leftdirs:>", "((.*/)|(.*\\\\)|())");
            matchFileExpression.Replace("<:rightdirs:>", ".*");
            matchFileExpression.Replace("<:anything:>", ".*");
            matchFileExpression.Replace("<:anythingbutdot:>", "[^\\.]*");
            matchFileExpression.Replace("<:anysinglecharacterbutdot:>", "[^\\.].");
            matchFileExpression.Replace("<:anynonseparator:>", "[^/\\\\]*");
            matchFileExpression.Replace("<:singlecharacter:>", ".");
            matchFileExpression.Replace("<:dirseparator:>", "[/\\\\]+");
            matchFileExpression.Replace("<:uncslashslash:>", @"\\\\");
            matchFileExpression.Replace("<:bol:>", "^");
            matchFileExpression.Replace("<:eol:>", "$");
            matchFileExpression.Replace("<:fixeddir:>", "(?<FIXEDDIR>");
            matchFileExpression.Replace("<:endfixeddir:>", ")");
            matchFileExpression.Replace("<:wildcarddir:>", "(?<WILDCARDDIR>");
            matchFileExpression.Replace("<:endwildcarddir:>", ")");
            matchFileExpression.Replace("<:filename:>", "(?<FILENAME>");
            matchFileExpression.Replace("<:endfilename:>", ")");

            return matchFileExpression.ToString();
        }

        /// <summary>
        /// Given a filespec, get the information needed for file matching. 
        /// </summary>
        /// <param name="filespec">The filespec.</param>
        /// <param name="regexFileMatch">Receives the regular expression.</param>
        /// <param name="needsRecursion">Receives the flag that is true if recursion is required.</param>
        /// <param name="isLegalFileSpec">Receives the flag that is true if the filespec is legal.</param>
        /// <param name="getFileSystemEntries">Delegate.</param>
        internal static void GetFileSpecInfoWithRegexObject
        (
            string filespec,
            out Regex regexFileMatch,
            out bool needsRecursion,
            out bool isLegalFileSpec,
            GetFileSystemEntries getFileSystemEntries

        )
        {
            string fixedDirectoryPart;
            string wildcardDirectoryPart;
            string filenamePart;
            string matchFileExpression;

            GetFileSpecInfo(filespec,
                out fixedDirectoryPart, out wildcardDirectoryPart, out filenamePart,
                out matchFileExpression, out needsRecursion, out isLegalFileSpec,
                getFileSystemEntries);

            
            regexFileMatch = isLegalFileSpec
                ? new Regex(matchFileExpression, DefaultRegexOptions)
                : null;
        }

        internal delegate Tuple<string, string, string> FixupParts(
            string fixedDirectoryPart,
            string recursiveDirectoryPart,
            string filenamePart);

        /// <summary>
        /// Given a filespec, parse it and construct the regular expression string.
        /// </summary>
        /// <param name="filespec">The filespec.</param>
        /// <param name="fixedDirectoryPart">Receives the fixed directory part.</param>
        /// <param name="wildcardDirectoryPart">Receives the wildcard directory part.</param>
        /// <param name="filenamePart">Receives the filename part.</param>
        /// <param name="matchFileExpression">Receives the regular expression.</param>
        /// <param name="needsRecursion">Receives the flag that is true if recursion is required.</param>
        /// <param name="isLegalFileSpec">Receives the flag that is true if the filespec is legal.</param>
        /// <param name="getFileSystemEntries">Delegate.</param>
        /// <param name="fixupParts">hook method to further change the parts</param>
        internal static void GetFileSpecInfo
        (
            string filespec,
            out string fixedDirectoryPart,
            out string wildcardDirectoryPart,
            out string filenamePart,
            out string matchFileExpression,
            out bool needsRecursion,
            out bool isLegalFileSpec,
            GetFileSystemEntries getFileSystemEntries,
            FixupParts fixupParts = null
        )
        {
            isLegalFileSpec = true;
            needsRecursion = false;
            fixedDirectoryPart = String.Empty;
            wildcardDirectoryPart = String.Empty;
            filenamePart = String.Empty;
            matchFileExpression = null;

            if (!RawFileSpecIsValid(filespec))
            {
                isLegalFileSpec = false;
                return;
            }

            /*
             * Now break up the filespec into constituent parts--fixed, wildcard and filename.
             */
            SplitFileSpec(filespec, out fixedDirectoryPart, out wildcardDirectoryPart, out filenamePart, getFileSystemEntries);

            if (fixupParts != null)
            {
                var newParts = fixupParts(fixedDirectoryPart, wildcardDirectoryPart, filenamePart);

                // todo use named tuples when they'll be available
                fixedDirectoryPart = newParts.Item1;
                wildcardDirectoryPart = newParts.Item2;
                filenamePart = newParts.Item3;
            }

            /*
             *  Get a regular expression for matching files that will be found.
             */
            matchFileExpression = RegularExpressionFromFileSpec(fixedDirectoryPart, wildcardDirectoryPart, filenamePart, out isLegalFileSpec);

            /*
             * Was the filespec valid? If not, then just return now.
             */
            if (!isLegalFileSpec)
            {
                return;
            }

            /*
             * Determine whether recursion will be required.
             */
            needsRecursion = (wildcardDirectoryPart.Length != 0);
        }

        internal static bool RawFileSpecIsValid(string filespec)
        {
            // filespec cannot contain illegal characters
            if (-1 != filespec.IndexOfAny(s_invalidPathChars))
            {
                return false;
            }

            /*
             * Check for patterns in the filespec that are explicitly illegal.
             * 
             * Any path with "..." in it is illegal.
             */
            if (-1 != filespec.IndexOf("...", StringComparison.Ordinal))
            {
                return false;
            }

            /*
             * If there is a ':' anywhere but the second character, this is an illegal pattern.
             * Catches this case among others,
             * 
             *        http://www.website.com
             * 
             */
            int rightmostColon = filespec.LastIndexOf(":", StringComparison.Ordinal);

            if
            (
                -1 != rightmostColon
                && 1 != rightmostColon
            )
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// The results of a match between a filespec and a file name.
        /// </summary>
        internal sealed class Result
        {
            /// <summary>
            /// Default constructor.
            /// </summary>
            internal Result()
            {
                // do nothing
            }

            internal bool isLegalFileSpec; // initially false
            internal bool isMatch; // initially false
            internal bool isFileSpecRecursive; // initially false
            internal string fixedDirectoryPart = String.Empty;
            internal string wildcardDirectoryPart = String.Empty;
            internal string filenamePart = String.Empty;
        }

        /// <summary>
        /// A wildcard (* and ?) matching algorithm that tests whether the input string matches against the pattern.
        /// </summary>
        /// <remarks>Source: http://www.c-sharpcorner.com/uploadfile/b81385/efficient-string-matching-algorithm-with-use-of-wildcard-characters/</remarks>
        /// <param name="input">String which is matched against the pattern.</param>
        /// <param name="pattern">Pattern against which string is matched.</param>
        internal static bool IsMatch(string input, string pattern)
        {
            if (pattern == "*")
            {
                return true;
            }
            int[] inputPosStack = new int[(input.Length + 1) * (pattern.Length + 1)];   // Stack containing input positions that should be tested for further matching
            int[] patternPosStack = new int[inputPosStack.Length];                      // Stack containing pattern positions that should be tested for further matching
            int stackPos = -1;                                                          // Points to last occupied entry in stack; -1 indicates that stack is empty
            bool[,] pointTested = new bool[input.Length + 1, pattern.Length + 1];       // Each true value indicates that input position vs. pattern position has been tested
            int inputPos = 0;   // Position in input matched up to the first multiple wildcard in pattern
            int patternPos = 0; // Position in pattern matched up to the first multiple wildcard in pattern
            // Match beginning of the string until first multiple wildcard in pattern
            while (inputPos < input.Length && patternPos < pattern.Length && pattern[patternPos] != '*' && (input[inputPos] == pattern[patternPos] || pattern[patternPos] == '?'))
            {
                inputPos++;
                patternPos++;
            }
            // Push this position to stack if it points to end of pattern or to a general wildcard
            if (patternPos == pattern.Length || pattern[patternPos] == '*')
            {
                pointTested[inputPos, patternPos] = true;
                inputPosStack[++stackPos] = inputPos;
                patternPosStack[stackPos] = patternPos;
            }
            bool matched = false;
            // Repeat matching until either string is matched against the pattern or no more parts remain on stack to test
            while (stackPos >= 0 && !matched)
            {
                inputPos = inputPosStack[stackPos];         // Pop input and pattern positions from stack
                patternPos = patternPosStack[stackPos--];   // Matching will succeed if rest of the input string matches rest of the pattern
                if (inputPos == input.Length && (patternPos == pattern.Length || (patternPos == pattern.Length - 1 && pattern[patternPos] == '*')))
                    matched = true;     // Reached end of both pattern and input string, hence matching is successful
                else
                {
                    // First character in next pattern block is guaranteed to be multiple wildcard
                    // So skip it and search for all matches in value string until next multiple wildcard character is reached in pattern
                    for (int curInputStart = inputPos; curInputStart < input.Length; curInputStart++)
                    {
                        int curInputPos = curInputStart;
                        int curPatternPos = patternPos + 1;
                        if (curPatternPos == pattern.Length)
                        {   // Pattern ends with multiple wildcard, hence rest of the input string is matched with that character
                            curInputPos = input.Length;
                        }
                        else
                        {
                            while (curInputPos < input.Length && curPatternPos < pattern.Length && pattern[curPatternPos] != '*' &&
                                   (input[curInputPos] == pattern[curPatternPos] || pattern[curPatternPos] == '?'))
                            {
                                curInputPos++;
                                curPatternPos++;
                            }
                        }
                        // If we have reached next multiple wildcard character in pattern without breaking the matching sequence, then we have another candidate for full match
                        // This candidate should be pushed to stack for further processing
                        // At the same time, pair (input position, pattern position) will be marked as tested, so that it will not be pushed to stack later again
                        if (((curPatternPos == pattern.Length && curInputPos == input.Length) || (curPatternPos < pattern.Length && pattern[curPatternPos] == '*'))
                            && !pointTested[curInputPos, curPatternPos])
                        {
                            pointTested[curInputPos, curPatternPos] = true;
                            inputPosStack[++stackPos] = curInputPos;
                            patternPosStack[stackPos] = curPatternPos;
                        }
                    }
                }
            }
            return matched;
        }

        /// <summary>
        /// Given a pattern (filespec) and a candidate filename (fileToMatch)
        /// return matching information.
        /// </summary>
        /// <param name="filespec">The filespec.</param>
        /// <param name="fileToMatch">The candidate to match against.</param>
        /// <returns>The result class.</returns>
        internal static Result FileMatch
        (
            string filespec,
            string fileToMatch
        )
        {
            Result matchResult = new Result();

            fileToMatch = GetLongPathName(fileToMatch, s_defaultGetFileSystemEntries);

            Regex regexFileMatch;
            GetFileSpecInfoWithRegexObject
            (
                filespec,
                out regexFileMatch,
                out matchResult.isFileSpecRecursive,
                out matchResult.isLegalFileSpec,
                s_defaultGetFileSystemEntries
            );

            if (matchResult.isLegalFileSpec)
            {
                GetRegexMatchInfo(
                    fileToMatch,
                    regexFileMatch,
                    out matchResult.isMatch,
                    out matchResult.fixedDirectoryPart,
                    out matchResult.wildcardDirectoryPart,
                    out matchResult.filenamePart);
            }

            return matchResult;
        }

        internal static void GetRegexMatchInfo(
            string fileToMatch,
            Regex fileSpecRegex,
            out bool isMatch,
            out string fixedDirectoryPart,
            out string wildcardDirectoryPart,
            out string filenamePart)
        {
            Match match = fileSpecRegex.Match(fileToMatch);

            isMatch = match.Success;
            fixedDirectoryPart = string.Empty;
            wildcardDirectoryPart = String.Empty;
            filenamePart = string.Empty;

            if (isMatch)
            {
                fixedDirectoryPart = match.Groups["FIXEDDIR"].Value;
                wildcardDirectoryPart = match.Groups["WILDCARDDIR"].Value;
                filenamePart = match.Groups["FILENAME"].Value;
            }
        }

        /// <summary>
        /// Given a filespec, find the files that match. 
        /// Will never throw IO exceptions: if there is no match, returns the input verbatim.
        /// </summary>
        /// <param name="projectDirectoryUnescaped">The project directory.</param>
        /// <param name="filespecUnescaped">Get files that match the given file spec.</param>
        /// <param name="excludeSpecsUnescaped">Exclude files that match this file spec.</param>
        /// <returns>The array of files.</returns>
        internal static string[] GetFiles
        (
            string projectDirectoryUnescaped,
            string filespecUnescaped,
            IEnumerable<string> excludeSpecsUnescaped = null
        )
        {
            // Possible future improvement: make sure file existence caching happens only at evaluation time, and maybe only within a build session. https://github.com/Microsoft/msbuild/issues/2306
            if (Traits.Instance.MSBuildCacheFileEnumerations)
            {
                string filesKey = ComputeFileEnumerationCacheKey(projectDirectoryUnescaped, filespecUnescaped, excludeSpecsUnescaped);
                string[] files;
                if (!s_cachedFileEnumerations.Value.TryGetValue(filesKey, out files))
                {
                    // avoid parallel evaluations of the same wildcard by using a unique lock for each wildcard
                    object locks = s_cachedFileEnumerationsLock.Value.GetOrAdd(filesKey, _ => new object());
                    lock (locks)
                    {
                        if (!s_cachedFileEnumerations.Value.TryGetValue(filesKey, out files))
                        {
                            files =
                                s_cachedFileEnumerations.Value.GetOrAdd(
                                    filesKey,
                                    (_) =>
                                        GetFiles(
                                            projectDirectoryUnescaped,
                                            filespecUnescaped,
                                            excludeSpecsUnescaped,
                                            s_defaultGetFileSystemEntries,
                                            s_defaultDirectoryExists));
                        }
                    }
                }

                // Copy the file enumerations to prevent outside modifications of the cache (e.g. sorting, escaping) and to maintain the original contract that a new array is created on each call.
                var filesToReturn = new string[files.Length];
                Array.Copy(files, filesToReturn, files.Length);
                return filesToReturn;
            }
            else
            {
                string[] files = GetFiles(
                    projectDirectoryUnescaped,
                    filespecUnescaped,
                    excludeSpecsUnescaped,
                    s_defaultGetFileSystemEntries,
                    s_defaultDirectoryExists);
                return files;
            }
        }

        private static string ComputeFileEnumerationCacheKey(string projectDirectoryUnescaped, string filespecUnescaped, IEnumerable<string> excludes)
        {
            var sb = new StringBuilder();

            sb.Append(projectDirectoryUnescaped);
            sb.Append(filespecUnescaped);

            if (excludes != null)
            {
                foreach (var exclude in excludes)
                {
                    sb.Append(exclude);
                }
            }

            return sb.ToString();
        }

        enum SearchAction
        {
            RunSearch,
            ReturnFileSpec,
            ReturnEmptyList,
        }

        static SearchAction GetFileSearchData(string projectDirectoryUnescaped, string filespecUnescaped,
            GetFileSystemEntries getFileSystemEntries, DirectoryExists directoryExists, out bool stripProjectDirectory,
            out RecursionState result)
        {
            stripProjectDirectory = false;
            result = new RecursionState();

            string fixedDirectoryPart;
            string wildcardDirectoryPart;
            string filenamePart;
            string matchFileExpression;
            bool needsRecursion;
            bool isLegalFileSpec;
            GetFileSpecInfo
            (
                filespecUnescaped,
                out fixedDirectoryPart,
                out wildcardDirectoryPart,
                out filenamePart,
                out matchFileExpression,
                out needsRecursion,
                out isLegalFileSpec,
                getFileSystemEntries
            );

            /*
             * If the filespec is invalid, then just return now.
             */
            if (!isLegalFileSpec)
            {
                return SearchAction.ReturnFileSpec;
            }

            // The projectDirectory is not null only if we are running the evaluation from
            // inside the engine (i.e. not from a task)
            if (projectDirectoryUnescaped != null)
            {
                if (fixedDirectoryPart != null)
                {
                    string oldFixedDirectoryPart = fixedDirectoryPart;
                    try
                    {
                        fixedDirectoryPart = Path.Combine(projectDirectoryUnescaped, fixedDirectoryPart);
                    }
                    catch (ArgumentException)
                    {
                        return SearchAction.ReturnEmptyList;
                    }

                    stripProjectDirectory = !String.Equals(fixedDirectoryPart, oldFixedDirectoryPart, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    fixedDirectoryPart = projectDirectoryUnescaped;
                    stripProjectDirectory = true;
                }
            }

            /*
             * If the fixed directory part doesn't exist, then this means no files should be
             * returned.
             */
            if (fixedDirectoryPart.Length > 0 && !directoryExists(fixedDirectoryPart))
            {
                return SearchAction.ReturnEmptyList;
            }

            // determine if we need to use the regular expression to match the files
            // PERF NOTE: Constructing a Regex object is expensive, so we avoid it whenever possible
            bool matchWithRegex =
                // if we have a directory specification that uses wildcards, and
                (wildcardDirectoryPart.Length > 0) &&
                // the specification is not a simple "**"
                !IsRecursiveDirectoryMatch(wildcardDirectoryPart);
            // then we need to use the regular expression

            var searchData = new FilesSearchData(
                // if using the regular expression, ignore the file pattern
                (matchWithRegex ? null : filenamePart),
                // if using the file pattern, ignore the regular expression
                (matchWithRegex ? new Regex(matchFileExpression, RegexOptions.IgnoreCase) : null),
                needsRecursion);

            result.SearchData = searchData;
            result.BaseDirectory = fixedDirectoryPart;
            result.RemainingWildcardDirectory = wildcardDirectoryPart;

            return SearchAction.RunSearch;
        }

        static string[] CreateArrayWithSingleItemIfNotExcluded(string filespecUnescaped, IEnumerable<string> excludeSpecsUnescaped)
        {
            if (excludeSpecsUnescaped != null)
            {
                foreach (string excludeSpec in excludeSpecsUnescaped)
                {

                    // Try a path equality check first to:
                    // - avoid the expensive regex
                    // - maintain legacy behaviour where an illegal filespec is treated as a normal string
                    if (FileUtilities.PathsEqual(filespecUnescaped, excludeSpec))
                    {
                        return Array.Empty<string>();
                    }

                    var match = FileMatch(excludeSpec, filespecUnescaped);

                    if (match.isLegalFileSpec && match.isMatch)
                    {
                        return Array.Empty<string>();
                    }
                }
            }
            return new[] { filespecUnescaped };
        }

        /// <summary>
        /// Given a filespec, find the files that match. 
        /// Will never throw IO exceptions: if there is no match, returns the input verbatim.
        /// </summary>
        /// <param name="projectDirectoryUnescaped">The project directory.</param>
        /// <param name="filespecUnescaped">Get files that match the given file spec.</param>
        /// <param name="excludeSpecsUnescaped">Exclude files that match this file spec.</param>
        /// <param name="getFileSystemEntries">Get files that match the given file spec.</param>
        /// <param name="directoryExists">Determine whether a directory exists.</param>
        /// <returns>The array of files.</returns>
        internal static string[] GetFiles
        (
            string projectDirectoryUnescaped,
            string filespecUnescaped,
            IEnumerable<string> excludeSpecsUnescaped,
            GetFileSystemEntries getFileSystemEntries,
            DirectoryExists directoryExists
        )
        {
            // For performance. Short-circuit iff there is no wildcard.
            // Perf Note: Doing a [Last]IndexOfAny(...) is much faster than compiling a
            // regular expression that does the same thing, regardless of whether
            // filespec contains one of the characters.
            // Choose LastIndexOfAny instead of IndexOfAny because it seems more likely
            // that wildcards will tend to be towards the right side.
            if (!HasWildcards(filespecUnescaped))
            {
                return CreateArrayWithSingleItemIfNotExcluded(filespecUnescaped, excludeSpecsUnescaped);
            }

            // UNDONE (perf): Short circuit the complex processing when we only have a path and a wildcarded filename

            /*
             * Analyze the file spec and get the information we need to do the matching.
             */
            bool stripProjectDirectory;
            RecursionState state;
            var action = GetFileSearchData(projectDirectoryUnescaped, filespecUnescaped, getFileSystemEntries, directoryExists,
                out stripProjectDirectory, out state);

            if (action == SearchAction.ReturnEmptyList)
            {
                return Array.Empty<string>();
            }
            else if (action == SearchAction.ReturnFileSpec)
            {
                return CreateArrayWithSingleItemIfNotExcluded(filespecUnescaped, excludeSpecsUnescaped);
            }
            else if (action != SearchAction.RunSearch)
            {
                //  This means the enum value wasn't valid (or a new one was added without updating code correctly)
                throw new NotSupportedException(action.ToString());
            }

            List<RecursionState> searchesToExclude = null;

            //  Exclude searches which will become active when the recursive search reaches their BaseDirectory.
            //  The BaseDirectory of the exclude search is the key for this dictionary.
            Dictionary<string, List<RecursionState>> searchesToExcludeInSubdirs = null;

            HashSet<string> resultsToExclude = null;
            if (excludeSpecsUnescaped != null)
            {
                searchesToExclude = new List<RecursionState>();
                foreach (string excludeSpec in excludeSpecsUnescaped)
                {
                    //  This is ignored, we always use the include pattern's value for stripProjectDirectory
                    bool excludeStripProjectDirectory;

                    RecursionState excludeState;
                    var excludeAction = GetFileSearchData(projectDirectoryUnescaped, excludeSpec, getFileSystemEntries, directoryExists,
                        out excludeStripProjectDirectory, out excludeState);

                    if (excludeAction == SearchAction.ReturnFileSpec)
                    {
                        if (resultsToExclude == null)
                        {
                            resultsToExclude = new HashSet<string>();
                        }
                        resultsToExclude.Add(excludeSpec);

                        continue;
                    }
                    else if (excludeAction == SearchAction.ReturnEmptyList)
                    {
                        //  Nothing to do
                        continue;
                    }
                    else if (excludeAction != SearchAction.RunSearch)
                    {
                        //  This means the enum value wasn't valid (or a new one was added without updating code correctly)
                        throw new NotSupportedException(excludeAction.ToString());
                    }

                    var excludeBaseDirectoryNormalized = excludeState.BaseDirectory.NormalizeForPathComparison();
                    var includeBaseDirectoryNormalized = state.BaseDirectory.NormalizeForPathComparison();

                    if (excludeBaseDirectoryNormalized != includeBaseDirectoryNormalized)
                    {
                        //  What to do if the BaseDirectory for the exclude search doesn't match the one for inclusion?
                        //  - If paths don't match (one isn't a prefix of the other), then ignore the exclude search.  Examples:
                        //      - c:\Foo\ - c:\Bar\
                        //      - c:\Foo\Bar\ - C:\Foo\Baz\
                        //      - c:\Foo\ - c:\Foo2\
                        if (excludeBaseDirectoryNormalized.Length == includeBaseDirectoryNormalized.Length)
                        {
                            //  Same length, but different paths.  Ignore this exclude search
                            continue;
                        }
                        else if (excludeBaseDirectoryNormalized.Length > includeBaseDirectoryNormalized.Length)
                        {
                            if (!excludeBaseDirectoryNormalized.StartsWith(includeBaseDirectoryNormalized))
                            {
                                //  Exclude path is longer, but doesn't start with include path.  So ignore it.
                                continue;
                            }

                            //  - The exclude BaseDirectory is somewhere under the include BaseDirectory. So
                            //    keep the exclude search, but don't do any processing on it while recursing until the baseDirectory
                            //    in the recursion matches the exclude BaseDirectory.  Examples:
                            //      - Include - Exclude
                            //      - C:\git\msbuild\ - c:\git\msbuild\obj\
                            //      - C:\git\msbuild\ - c:\git\msbuild\src\Common\

                            if (searchesToExcludeInSubdirs == null)
                            {
                                searchesToExcludeInSubdirs = new Dictionary<string, List<RecursionState>>();
                            }
                            List<RecursionState> listForSubdir;
                            if (!searchesToExcludeInSubdirs.TryGetValue(excludeBaseDirectoryNormalized, out listForSubdir))
                            {
                                listForSubdir = new List<RecursionState>();

                                // The normalization fixes https://github.com/Microsoft/msbuild/issues/917
                                // and is a partial fix for https://github.com/Microsoft/msbuild/issues/724
                                searchesToExcludeInSubdirs[excludeBaseDirectoryNormalized] = listForSubdir;
                            }
                            listForSubdir.Add(excludeState);
                        }
                        else
                        {
                            //  Exclude base directory length is less than include base directory length.
                            if (!state.BaseDirectory.StartsWith(excludeState.BaseDirectory))
                            {
                                //  Include path is longer, but doesn't start with the exclude path.  So ignore exclude path
                                //  (since it won't match anything under the include path)
                                continue;
                            }

                            //  Now check the wildcard part
                            if (excludeState.RemainingWildcardDirectory.Length == 0)
                            {
                                //  The wildcard part is empty, so ignore the exclude search, as it's looking for files non-recursively
                                //  in a folder higher up than the include baseDirectory.
                                //  Example: include="c:\git\msbuild\src\Framework\**\*.cs" exclude="c:\git\msbuild\*.cs"
                                continue;
                            }
                            else if (IsRecursiveDirectoryMatch(excludeState.RemainingWildcardDirectory))
                            {
                                //  The wildcard part is exactly "**\", so the exclude pattern will apply to everything in the include
                                //  pattern, so simply update the exclude's BaseDirectory to be the same as the include baseDirectory
                                //  Example: include="c:\git\msbuild\src\Framework\**\*.*" exclude="c:\git\msbuild\**\*.bak"
                                excludeState.BaseDirectory = state.BaseDirectory;
                                searchesToExclude.Add(excludeState);
                            }
                            else
                            {
                                //  The wildcard part is non-empty and not "**\", so we will need to match it with a Regex.  Fortunately
                                //  these conditions mean that it needs to be matched with a Regex anyway, so here we will update the
                                //  BaseDirectory to be the same as the exclude BaseDirectory, and change the wildcard part to be "**\"
                                //  because we don't know where the different parts of the exclude wildcard part would be matched.
                                //  Example: include="c:\git\msbuild\src\Framework\**\*.*" exclude="c:\git\msbuild\**\bin\**\*.*"
                                Debug.Assert(excludeState.SearchData.RegexFileMatch != null, "Expected Regex to be used for exclude file matching");
                                excludeState.BaseDirectory = state.BaseDirectory;
                                excludeState.RemainingWildcardDirectory = recursiveDirectoryMatch + s_directorySeparator;
                                searchesToExclude.Add(excludeState);
                            }
                        }
                    }
                    else
                    {
                        searchesToExclude.Add(excludeState);
                    }
                }
            }

            if (searchesToExclude != null && searchesToExclude.Count == 0)
            {
                searchesToExclude = null;
            }

            /*
             * Even though we return a string[] we work internally with an IList.
             * This is because it's cheaper to add items to an IList and this code
             * might potentially do a lot of that.
             */
            var listOfFiles = new List<string>();

            /*
             * Now get the files that match, starting at the lowest fixed directory.
             */
            try
            {
                GetFilesRecursive(
                    listOfFiles,
                    state,
                    projectDirectoryUnescaped,
                    stripProjectDirectory,
                    getFileSystemEntries,
                    searchesToExclude,
                    searchesToExcludeInSubdirs);
            }
            catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
            {
                // Assume it's not meant to be a path
                return CreateArrayWithSingleItemIfNotExcluded(filespecUnescaped, excludeSpecsUnescaped);
            }

            /*
             * Build the return array.
             */
            var files = resultsToExclude != null
                ? listOfFiles.Where(f => !resultsToExclude.Contains(f)).ToArray()
                : listOfFiles.ToArray();

            return files;
        }

        private static bool IsRecursiveDirectoryMatch(string path) => path.TrimTrailingSlashes() == recursiveDirectoryMatch;
    }
}
