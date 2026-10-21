using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DROPME.Services
{
    /// <summary>
    /// Хранит опубликованные Windows-пути. Системные корни всегда присутствуют,
    /// пользовательские папки можно добавлять и удалять.
    /// </summary>
    internal static class WindowsStorageService
    {
        internal sealed class StorageRoot
        {
            public string Id { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string Path { get; init; } = string.Empty;
            public string Description { get; init; } = string.Empty;
            public bool IsEnabled { get; init; }
            public bool IsDriveRoot { get; init; }
            public bool IsAvailable { get; init; }
            public bool CanRemove { get; init; }
        }

        /// <summary>
        /// Возвращает доступные хранилища в порядке: локальные диски, профиль пользователя,
        /// затем пользовательские каталоги.
        /// </summary>
        public static IReadOnlyList<StorageRoot> GetRoots()
        {
            HashSet<string> enabled = ParseEnabledRootIds();
            List<StorageRoot> roots = new List<StorageRoot>();

            foreach (DriveInfo drive in DriveInfo.GetDrives()
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                string id = "drive-" + drive.Name.TrimEnd('\\').TrimEnd(':').ToUpperInvariant();
                string volume = drive.DriveType.ToString();
                string format = string.Empty;
                if (drive.IsReady)
                {
                    volume = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.DriveType.ToString()
                        : drive.VolumeLabel;
                    format = drive.DriveFormat;
                }

                roots.Add(new StorageRoot
                {
                    Id = id,
                    DisplayName = string.Format(
                        LocalizationService.Text("storage_drive"),
                        drive.Name.TrimEnd('\\')),
                    Path = Path.GetFullPath(drive.RootDirectory.FullName),
                    Description = format.Length == 0 ? volume : volume + " • " + format,
                    IsEnabled = enabled.Contains(id),
                    IsDriveRoot = true,
                    IsAvailable = drive.IsReady,
                    CanRemove = false
                });
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile))
            {
                roots.Add(new StorageRoot
                {
                    Id = "profile",
                    DisplayName = LocalizationService.Text("storage_user_profile"),
                    Path = Path.GetFullPath(profile),
                    Description = string.Empty,
                    IsEnabled = enabled.Contains("profile"),
                    IsDriveRoot = false,
                    IsAvailable = true,
                    CanRemove = false
                });
            }

            foreach (string customPath in ParseCustomRootPaths())
            {
                if (!Directory.Exists(customPath))
                {
                    continue;
                }

                string id = BuildCustomRootId(customPath);
                DirectoryInfo directory = new DirectoryInfo(customPath);
                roots.Add(new StorageRoot
                {
                    Id = id,
                    DisplayName = directory.Name.Length == 0 ? directory.FullName : directory.Name,
                    Path = directory.FullName,
                    Description = string.Empty,
                    IsEnabled = enabled.Contains(id),
                    IsDriveRoot = false,
                    IsAvailable = true,
                    CanRemove = true
                });
            }

            return MakeDisplayNamesUnique(roots);
        }

        public static IReadOnlyList<StorageRoot> GetPublishedRoots()
        {
            return GetRoots().Where(root => root.IsEnabled && root.IsAvailable).ToList();
        }

        public static void SetEnabled(string rootId, bool enabled)
        {
            HashSet<string> ids = ParseEnabledRootIds();
            if (enabled)
            {
                ids.Add(rootId);
            }
            else
            {
                ids.Remove(rootId);
            }

            AppSettings.PublishedStorageRootIds = string.Join(
                ";",
                ids.OrderBy(id => id, StringComparer.Ordinal));
            AppSettings.Save();
        }

        public static void AddCustomRoot(string path)
        {
            string normalized = Path.GetFullPath(path);
            if (!Directory.Exists(normalized))
            {
                return;
            }

            List<string> roots = ParseCustomRootPaths().ToList();
            if (!roots.Any(existing =>
                    string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(normalized);
                SaveCustomRootPaths(roots);
            }

            SetEnabled(BuildCustomRootId(normalized), true);
        }

        public static void RemoveCustomRoot(string rootId)
        {
            List<string> roots = ParseCustomRootPaths()
                .Where(path => !string.Equals(
                    BuildCustomRootId(path),
                    rootId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            SaveCustomRootPaths(roots);
            SetEnabled(rootId, false);
        }

        private static IReadOnlyList<StorageRoot> MakeDisplayNamesUnique(List<StorageRoot> roots)
        {
            Dictionary<string, int> seen =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<StorageRoot> result = new List<StorageRoot>(roots.Count);
            foreach (StorageRoot root in roots)
            {
                string baseName = root.DisplayName;
                seen.TryGetValue(baseName, out int count);
                count++;
                seen[baseName] = count;
                result.Add(new StorageRoot
                {
                    Id = root.Id,
                    DisplayName = count == 1 ? baseName : baseName + " (" + count + ")",
                    Path = root.Path,
                    Description = root.Description,
                    IsEnabled = root.IsEnabled,
                    IsDriveRoot = root.IsDriveRoot,
                    IsAvailable = root.IsAvailable,
                    CanRemove = root.CanRemove
                });
            }
            return result;
        }

        private static HashSet<string> ParseEnabledRootIds()
        {
            return new HashSet<string>(
                AppSettings.PublishedStorageRootIds
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ParseCustomRootPaths()
        {
            foreach (string encoded in AppSettings.CustomStorageRoots
                         .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                    continue;
                }

                string path = Encoding.UTF8.GetString(bytes);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return Path.GetFullPath(path);
                }
            }
        }

        private static void SaveCustomRootPaths(IEnumerable<string> paths)
        {
            AppSettings.CustomStorageRoots = string.Join(
                ";",
                paths.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => Convert.ToBase64String(Encoding.UTF8.GetBytes(path))));
            AppSettings.Save();
        }

        private static string BuildCustomRootId(string path)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(path).ToUpperInvariant()));
            return "custom-" + Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
        }
    }
}
