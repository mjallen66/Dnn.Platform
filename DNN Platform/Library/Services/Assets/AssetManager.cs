﻿#region Copyright
// 
// DotNetNuke® - http://www.dotnetnuke.com
// Copyright (c) 2002-2014
// by DotNetNuke Corporation
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated 
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and 
// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or substantial portions 
// of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED 
// TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL 
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF 
// CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

using DotNetNuke.Common;
using DotNetNuke.Common.Utilities;
using DotNetNuke.ComponentModel;
using DotNetNuke.Entities.Host;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Security.Permissions;
using DotNetNuke.Services.FileSystem;

namespace DotNetNuke.Services.Assets
{
    public class AssetManager : ComponentBase<IAssetManager, AssetManager>, IAssetManager
    {
        // TODO: Use correct resx
        private const string ResourceFile = "DesktopModules/DigitalAssets/App_LocalResources/SharedResources";

        public ContentPage GetFolderContent(int folderId, int startIndex, int numItems, string sortExpression =  null, SubfolderFilter subfolderFilter = SubfolderFilter.IncludeSubfoldersFolderStructure)
        {
            var folder = GetFolderInfo(folderId);

            if (!FolderPermissionController.CanBrowseFolder((FolderInfo)folder))
            {
                throw new AssetManagerException("The user has no permission to browse the folder"); // TODO: to resx
            }

            var sortProperties = SortProperties.Parse(sortExpression);

            List<IFolderInfo> folders;

            if (subfolderFilter != SubfolderFilter.IncludeSubfoldersFolderStructure)
            {
                folders = new List<IFolderInfo>();
            }
            else
            {
                folders = GetFolders(folder, sortProperties.Column == "ItemName" ? "FolderName" : sortProperties.Column, sortProperties.Ascending).ToList();
            }

            var recursive = subfolderFilter == SubfolderFilter.IncludeSubfoldersFilesOnly;
            var files = GetFiles(folder, sortProperties, startIndex, recursive).ToList();

            IEnumerable<object> content;
            if (startIndex + numItems <= folders.Count())
            {
                content = folders.Skip(startIndex).Take(numItems);
            }
            else if (startIndex >= folders.Count())
            {
                content = files.Skip(startIndex - folders.Count).Take(numItems);
            }
            else
            {
                var numFiles = numItems - (folders.Count - startIndex);
                content = folders.Skip(startIndex);
                content = content.Union(files.Take(numFiles));
            }

            return new ContentPage
            {
                Folder = folder,
                Items = content.ToList(),
                TotalCount = folders.Count() + files.Count()
            };
        }

        public ContentPage SearchFolderContent(int folderId, string pattern, int startIndex, int numItems, string sortExpression = null, SubfolderFilter subfolderFilter = SubfolderFilter.IncludeSubfoldersFolderStructure)
        {
            var recursive = subfolderFilter != SubfolderFilter.ExcludeSubfolders;
            var folder = GetFolderInfo(folderId);

            var files = FolderManager.Instance.SearchFiles(folder, pattern, recursive);
            var sortProperties = SortProperties.Parse(sortExpression);
            var sortedFiles = SortFiles(files, sortProperties).ToList();

            IEnumerable<object> content = sortedFiles.Skip(startIndex).Take(numItems);

            return new ContentPage
            {
                Folder = folder,
                Items = content.ToList(),
                TotalCount = sortedFiles.Count()
            };
        }

        public IEnumerable<IFolderInfo> GetFolders(IFolderInfo parentFolder, string orderingField, bool asc)
        {
            Requires.NotNull("parentFolder", parentFolder);

            var folders = FolderManager.Instance.GetFolders(parentFolder).Where(f => HasPermission(f, "BROWSE") || HasPermission(f, "READ"));

            // Set default sorting values
            var field = string.IsNullOrEmpty(orderingField) ? "FolderName" : orderingField;

            return ApplyOrder(folders.AsQueryable(), field, asc);
        } 

        private IEnumerable<IFileInfo> GetFiles(IFolderInfo folder, SortProperties sortProperties, int startIndex, bool recursive)
        {
            Requires.NotNull("folder", folder);

            if (Host.EnableFileAutoSync && startIndex == 0)
            {
                FolderManager.Instance.Synchronize(folder.PortalID, folder.FolderPath, false, true);
            }

            return SortFiles(FolderManager.Instance.GetFiles(folder, recursive, true), sortProperties);
        }

        private static IEnumerable<IFileInfo> SortFiles(IEnumerable<IFileInfo> files, SortProperties sortProperties)
        {
            switch (sortProperties.Column)
            {
                case "ItemName":
                    return OrderBy(files, f => f.FileName, sortProperties.Ascending);
                case "LastModifiedOnDate":
                    return OrderBy(files, f => f.LastModifiedOnDate, sortProperties.Ascending);
                case "Size":
                    return OrderBy(files, f => f.Size, sortProperties.Ascending);
                case "ParentFolder":
                    return OrderBy(files, f => f.FolderId, new FolderPathComparer(), sortProperties.Ascending);
                default:
                    return files;
            }
        }

        public static IOrderedQueryable<T> ApplyOrder<T>(IQueryable<T> source, string propertyName, bool asc)
        {
            var methodName = asc ? "OrderBy" : "OrderByDescending";
            var arg = Expression.Parameter(typeof(T), "x");

            // Use reflection to mirror LINQ
            var property = typeof(T).GetProperty(propertyName);

            // If property is undefined returns the original source
            if (property == null) return (IOrderedQueryable<T>)source;

            Expression expr = Expression.Property(arg, property);

            var delegateType = typeof(Func<,>).MakeGenericType(typeof(T), property.PropertyType);
            var lambda = Expression.Lambda(delegateType, expr, arg);

            var result = typeof(Queryable).GetMethods().Single(
                    method => method.Name == methodName
                            && method.IsGenericMethodDefinition
                            && method.GetGenericArguments().Length == 2
                            && method.GetParameters().Length == 2)
                    .MakeGenericMethod(typeof(T), property.PropertyType)
                    .Invoke(null, new object[] { source, lambda });
            return (IOrderedQueryable<T>)result;
        }

        private static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(IEnumerable<TSource> source, Func<TSource, TKey> keySelector, bool ascending)
        {
            return ascending ? source.OrderBy(keySelector) : source.OrderByDescending(keySelector);
        }

        private static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer, bool ascending)
        {
            return ascending ? source.OrderBy(keySelector, comparer) : source.OrderByDescending(keySelector, comparer);
        }

        public IFileInfo RenameFile(int fileId, string newFileName)
        {
            Requires.NotNullOrEmpty("newFileName", newFileName);

            var filteredName = CleanDotsAtTheEndOfTheName(newFileName);

            if (string.IsNullOrEmpty(filteredName))
            {
                throw new AssetManagerException(GetLocalizedString("FolderFileNameHasInvalidcharacters.Error", newFileName));
            }

            // Chech if the new name has invalid chars
            if (IsInvalidName(filteredName))
            {
                throw new AssetManagerException(GetInvalidCharsErrorText());
            }

            // Check if the new name is a reserved name
            if (IsReservedName(filteredName))
            {
                throw new AssetManagerException(GetLocalizedString("FolderFileNameIsReserved.Error"));
            }

            var file = FileManager.Instance.GetFile(fileId, true);

            // Check if the name has not changed
            if (file.FileName == newFileName)
            {
                return file;
            }

            // Check if user has appropiate permissions
            var folder = FolderManager.Instance.GetFolder(file.FolderId);
            if (!HasPermission(folder, "MANAGE"))
            {
                throw new AssetManagerException(GetLocalizedString("UserHasNoPermissionToEditFile.Error"));
            }

            return FileManager.Instance.RenameFile(file, newFileName);
        }

        public IFolderInfo RenameFolder(int folderId, string newFolderName)
        {
            Requires.NotNullOrEmpty("newFolderName", newFolderName);

            newFolderName = CleanDotsAtTheEndOfTheName(newFolderName);

            // Check if the new name has invalid chars
            if (IsInvalidName(newFolderName))
            {
                throw new AssetManagerException(GetInvalidCharsErrorText());
            }

            // Check if the name is reserved
            if (IsReservedName(newFolderName))
            {
                throw new AssetManagerException(GetLocalizedString("FolderFileNameIsReserved.Error"));
            }

            var folder = GetFolderInfo(folderId);

            // Check if user has appropiate permissions
            if (!HasPermission(folder, "MANAGE"))
            {
                throw new AssetManagerException(GetLocalizedString("UserHasNoPermissionToEditFolder.Error"));
            }

            // check if the name has not changed
            if (folder.FolderName == newFolderName)
            {
                return folder;
            }
            if (folder.FolderName.ToLowerInvariant() == newFolderName.ToLowerInvariant())
            {
                folder.FolderPath = ReplaceFolderName(folder.FolderPath, folder.FolderName, newFolderName);
                return FolderManager.Instance.UpdateFolder(folder);
            }

            var newFolderPath = GetNewFolderPath(newFolderName, folder);
            // Check if the new folder already exists
            if (FolderManager.Instance.FolderExists(folder.PortalID, newFolderPath))
            {
                throw new AssetManagerException(GetLocalizedString("FolderAlreadyExists.Error", newFolderName));
            }

            FolderManager.Instance.RenameFolder(folder, newFolderName);
            return folder;
        }

        public IFolderInfo CreateFolder(string folderName, int folderParentId, int folderMappingId, string mappedPath)
        {
            Requires.NotNullOrEmpty("folderName", folderName);

            var filterFolderName = CleanDotsAtTheEndOfTheName(folderName);

            if (IsInvalidName(filterFolderName))
            {
                throw new AssetManagerException(GetInvalidCharsErrorText());
            }

            // Check if the new name is a reserved name
            if (IsReservedName(filterFolderName))
            {
                throw new AssetManagerException(GetLocalizedString("FolderFileNameIsReserved.Error"));
            }

            var parentFolder = GetFolderInfo(folderParentId);

            if (!HasPermission(parentFolder, "ADD"))
            {
                throw new AssetManagerException(GetLocalizedString("UserHasNoPermissionToAdd.Error"));
            }

            var folderPath = PathUtils.Instance.FormatFolderPath(
                PathUtils.Instance.FormatFolderPath(
                PathUtils.Instance.StripFolderPath(parentFolder.FolderPath).Replace("\\", "/")) + filterFolderName);

            mappedPath = PathUtils.Instance.FormatFolderPath(mappedPath);

            if (!Regex.IsMatch(mappedPath, @"^(?!\s*[\\/]).*$"))
            {
                throw new AssetManagerException(GetLocalizedString("InvalidMappedPath.Error"));
            }

            try
            {
                var folderMapping = FolderMappingController.Instance.GetFolderMapping(parentFolder.PortalID, folderMappingId);
                return FolderManager.Instance.AddFolder(folderMapping, folderPath, mappedPath.Replace("\\", "/"));                
            }
            catch (FolderAlreadyExistsException)
            {
                throw new AssetManagerException(GetLocalizedString("FolderAlreadyExists.Error", filterFolderName));
            }
        }

        public bool DeleteFolder(int folderId, bool onlyUnlink, ICollection<IFolderInfo> nonDeletedSubfolders)
        {
            var folder = FolderManager.Instance.GetFolder(folderId);
            if (folder == null) return false;

            if (!HasPermission(folder, "DELETE"))
            {
                nonDeletedSubfolders.Add(folder);
            }
            else
            {
                if (onlyUnlink)
                {
                    FolderManager.Instance.UnlinkFolder(folder);
                }
                else
                {
                    DeleteFolder(folder, nonDeletedSubfolders);
                }
            }

            return true;
        }

        private bool DeleteFolder(IFolderInfo folder, ICollection<IFolderInfo> nonDeletedItems)
        {
            var nonDeletedSubfolders = new List<IFolderInfo>();
            FolderManager.Instance.DeleteFolder(folder, nonDeletedSubfolders);
            if (!nonDeletedSubfolders.Any())
            {
                return false;
            }

            foreach (var nonDeletedSubfolder in nonDeletedSubfolders)
            {
                nonDeletedItems.Add(nonDeletedSubfolder);
            }
            return true;
        }

        public bool DeleteFile(int fileId)
        {
            var fileInfo = FileManager.Instance.GetFile(fileId, true);
            if (fileInfo == null) return false;

            var folder = FolderManager.Instance.GetFolder(fileInfo.FolderId);

            if (!HasPermission(folder, "DELETE"))
            {
                return false;
            }

            FileManager.Instance.DeleteFile(fileInfo);
            return true;
        }

        private static string CleanDotsAtTheEndOfTheName(string name)
        {
            return name.Trim().TrimEnd('.', ' ');
        }

        private bool IsInvalidName(string itemName)
        {
            var invalidFilenameChars = new Regex("[" + Regex.Escape(GetInvalidChars()) + "]");

            return invalidFilenameChars.IsMatch(itemName);
        }

        private string GetInvalidChars()
        {
            var invalidChars = new string(Path.GetInvalidFileNameChars());

            foreach (var ch in Path.GetInvalidPathChars())
            {
                if (invalidChars.IndexOf(ch) == -1) // The ch does not exists
                {
                    invalidChars += ch;
                }
            }

            return invalidChars;
        }

        private bool IsReservedName(string name)
        {
            var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CLOCK$" };
            return reservedNames.Contains(Path.GetFileNameWithoutExtension(name.ToUpperInvariant()));
        }

        private string GetInvalidCharsErrorText()
        {
            return GetLocalizedString("FolderFileNameHasInvalidcharacters.Error", "\\:/*?\"<>|");
        }

        private bool HasPermission(IFolderInfo folder, string permissionKey)
        {
            var hasPermision = PortalSettings.Current.UserInfo.IsSuperUser;

            if (!hasPermision && folder != null)
            {
                hasPermision = FolderPermissionController.HasFolderPermission(folder.FolderPermissions, permissionKey);
            }

            return hasPermision;
        }

        private IFolderInfo GetFolderInfo(int folderId)
        {
            var folder = FolderManager.Instance.GetFolder(folderId);
            if (folder == null)
            {
                throw new AssetManagerException(GetLocalizedString("FolderDoesNotExists.Error"));
            }
            return folder;
        }

        private string ReplaceFolderName(string path, string folderName, string newFolderName)
        {
            var newPath = PathUtils.Instance.RemoveTrailingSlash(path);
            if (string.IsNullOrEmpty(newPath))
            {
                return path;
            }
            var nameIndex = newPath.LastIndexOf(folderName, StringComparison.Ordinal);
            if (nameIndex == -1)
            {
                return path;
            }

            return newPath.Substring(0, nameIndex) + newPath.Substring(nameIndex).Replace(folderName, newFolderName);            
        }

        private string GetNewFolderPath(string newFolderName, IFolderInfo folder)
        {
            if (folder.FolderName.ToLowerInvariant() == newFolderName.ToLowerInvariant())
            {
                return folder.FolderPath;
            }

            var oldFolderPath = folder.FolderPath;
            if (oldFolderPath.Length > 0)
            {
                oldFolderPath = oldFolderPath.Substring(0, oldFolderPath.LastIndexOf(folder.FolderName, StringComparison.Ordinal));
            }

            return PathUtils.Instance.FormatFolderPath(oldFolderPath + newFolderName);
        }

        private static string GetLocalizedString(string key, params object[] args)
        {
            return string.Format(Localization.Localization.GetString(key, ResourceFile), args);
        }
    }
}
