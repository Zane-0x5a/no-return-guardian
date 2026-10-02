using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NoReturnGuardian.Core
{
    public sealed class SaveProfileCandidate
    {
        public string ProfilePath { get; set; }
        public string ProfileName { get; set; }
        public bool HasMarker { get; set; }
        public bool HasRunSlot { get; set; }
        public DateTime LastWriteUtc { get; set; }
    }

    public sealed class SaveLocator
    {
        private readonly string _documentsPath;

        public SaveLocator(string documentsPath)
        {
            _documentsPath = string.IsNullOrWhiteSpace(documentsPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : documentsPath;
        }

        public string GameDocumentsPath
        {
            get { return Path.Combine(_documentsPath, "The Last of Us Part II"); }
        }

        public IList<SaveProfileCandidate> DiscoverProfiles()
        {
            List<SaveProfileCandidate> candidates = new List<SaveProfileCandidate>();
            if (!Directory.Exists(GameDocumentsPath))
            {
                return candidates;
            }

            foreach (string directory in Directory.GetDirectories(GameDocumentsPath))
            {
                bool marker = File.Exists(
                    SaveLayout.CombineUnderProfile(directory, SaveLayout.MarkerPath));
                bool runSlot = File.Exists(
                    SaveLayout.CombineUnderProfile(directory, SaveLayout.RunDataPath));

                if (!marker && !runSlot)
                {
                    continue;
                }

                DateTime lastWrite = DateTime.MinValue;
                foreach (string relativePath in SaveLayout.RequiredFiles)
                {
                    string path = SaveLayout.CombineUnderProfile(directory, relativePath);
                    if (File.Exists(path))
                    {
                        DateTime value = File.GetLastWriteTimeUtc(path);
                        if (value > lastWrite)
                        {
                            lastWrite = value;
                        }
                    }
                }

                candidates.Add(new SaveProfileCandidate
                {
                    ProfilePath = directory,
                    ProfileName = Path.GetFileName(directory),
                    HasMarker = marker,
                    HasRunSlot = runSlot,
                    LastWriteUtc = lastWrite
                });
            }

            return candidates
                .OrderByDescending(item => item.HasMarker && item.HasRunSlot)
                .ThenByDescending(item => item.LastWriteUtc)
                .ToList();
        }

        public Result<string> ResolveProfile(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath)
                && Directory.Exists(configuredPath)
                && LooksLikeProfile(configuredPath))
            {
                return Result<string>.Ok(
                    Path.GetFullPath(configuredPath),
                    "configured_profile",
                    "Using the configured save profile.");
            }

            SaveProfileCandidate candidate = DiscoverProfiles().FirstOrDefault();
            if (candidate == null)
            {
                return Result<string>.Fail(
                    "profile_not_found",
                    "No The Last of Us Part II save profile was found in Windows Documents.");
            }

            return Result<string>.Ok(
                candidate.ProfilePath,
                "profile_discovered",
                "The most recent save profile was discovered automatically.");
        }

        public static bool LooksLikeProfile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            try
            {
                return File.Exists(SaveLayout.CombineUnderProfile(path, SaveLayout.MarkerPath))
                    || File.Exists(SaveLayout.CombineUnderProfile(path, SaveLayout.RunDataPath));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The profile a player picked: the folder itself when it is a profile, otherwise the profile under it whose
        /// run save was written last (the player picked the game's save root). Null when there is none.
        /// </summary>
        public static string ResolvePickedProfile(string path)
        {
            if (LooksLikeProfile(path))
            {
                return Path.GetFullPath(path);
            }

            try
            {
                return Directory.GetDirectories(path)
                    .Where(LooksLikeProfile)
                    .OrderByDescending(directory =>
                    {
                        string runData = SaveLayout.CombineUnderProfile(directory, SaveLayout.RunDataPath);
                        return File.Exists(runData) ? File.GetLastWriteTimeUtc(runData) : DateTime.MinValue;
                    })
                    .FirstOrDefault();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException)
            {
                return null;
            }
        }
    }
}

