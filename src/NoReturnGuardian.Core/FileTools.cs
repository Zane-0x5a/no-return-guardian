using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace NoReturnGuardian.Core
{
    public static class SaveLayout
    {
        public const string GameRunPath = "gamedata/R0A.save";
        public const string GameRunBackupPath = "gamedata/R0A.save-backup";
        public const string GameProfilePath = "gamedata/0P.save";
        public const string GameProfileBackupPath = "gamedata/0P.save-backup";
        public const string MarkerPath = "gamedata/nr.bin";
        public const string RunDataPath = "savedata/SAVEFILER0A/USR-DATA";
        public const string RunParamsPath = "savedata/SAVEFILER0A/params.json";
        public const string RunIconPath = "savedata/SAVEFILER0A/ICN-ID";
        public const string ProfileDataPath = "savedata/SAVEFILE0P/USR-DATA";
        public const string ProfileParamsPath = "savedata/SAVEFILE0P/params.json";
        public const string ProfileIconPath = "savedata/SAVEFILE0P/ICN-ID";
        public const string EmptyRunStateCode = "000000010000000000000000";
        public const int SlotEnvelopeLength = 36;

        public static readonly string[] RequiredFiles =
        {
            GameRunPath,
            GameRunBackupPath,
            GameProfilePath,
            GameProfileBackupPath,
            RunDataPath,
            RunParamsPath,
            RunIconPath,
            MarkerPath
        };

        public static readonly string[] WorkingProtectionFiles =
        {
            GameRunPath,
            GameRunBackupPath,
            GameProfilePath,
            GameProfileBackupPath,
            MarkerPath
        };

        public static readonly string[] CommittedSlotFiles =
        {
            RunDataPath,
            RunParamsPath,
            RunIconPath
        };

        public static readonly string[] ProfileSlotFiles =
        {
            ProfileDataPath,
            ProfileParamsPath,
            ProfileIconPath
        };

        public static readonly string[] NativeExportSourceFiles =
        {
            RunDataPath,
            RunParamsPath,
            RunIconPath,
            ProfileDataPath,
            ProfileParamsPath,
            ProfileIconPath,
            MarkerPath
        };

        public static readonly string[] LegacyRequiredFiles =
        {
            RunDataPath,
            RunParamsPath,
            RunIconPath,
            MarkerPath
        };

        public static readonly string[] RestoreOrder =
        {
            RunDataPath,
            RunParamsPath,
            RunIconPath,
            GameRunBackupPath,
            GameRunPath,
            GameProfileBackupPath,
            GameProfilePath,
            MarkerPath
        };

        public static bool IsAllowedRelativePath(string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            return RequiredFiles.Any(
                expected => string.Equals(expected, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsObservedSourceRelativePath(string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            return IsAllowedRelativePath(normalized)
                || ProfileSlotFiles.Any(
                    expected => string.Equals(
                        expected,
                        normalized,
                        StringComparison.OrdinalIgnoreCase));
        }

        public static string NormalizeRelativePath(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        public static string CombineUnderProfile(string profilePath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                throw new ArgumentException("Profile path is empty.", "profilePath");
            }

            string normalized = NormalizeRelativePath(relativePath);
            if (!IsObservedSourceRelativePath(normalized)
                || normalized.Contains("../")
                || normalized.Contains(":"))
            {
                throw new InvalidDataException("Unexpected save path: " + relativePath);
            }

            string root = Path.GetFullPath(profilePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(
                Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));

            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Save path escaped the selected profile.");
            }

            return candidate;
        }
    }

    public static class FileTools
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 16 * 1024 * 1024,
            RecursionLimit = 64
        };

        public static T ReadJson<T>(string path)
        {
            string text;
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                text = reader.ReadToEnd();
            }

            return Json.Deserialize<T>(text);
        }

        public static void WriteJsonAtomic<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string json = Json.Serialize(value);
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            AtomicReplace(temporary, path);
        }

        public static void AtomicReplace(string stagedPath, string targetPath)
        {
            string directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            const int maximumAttempts = 8;
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(targetPath))
                    {
                        File.Replace(stagedPath, targetPath, null, true);
                    }
                    else
                    {
                        File.Move(stagedPath, targetPath);
                    }

                    return;
                }
                catch (IOException)
                {
                    if (attempt == maximumAttempts)
                    {
                        throw;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt == maximumAttempts)
                    {
                        throw;
                    }
                }

                System.Threading.Thread.Sleep(attempt * 125);
            }
        }

        public static void CopyShared(string sourcePath, string destinationPath)
        {
            string directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (FileStream destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                source.CopyTo(destination, 1024 * 128);
                destination.Flush(true);
            }
        }

        public static string Sha256(string path, bool sharedRead)
        {
            FileShare share = sharedRead
                ? FileShare.ReadWrite | FileShare.Delete
                : FileShare.Read;
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                share))
            using (SHA256 hash = SHA256.Create())
            {
                return ToHex(hash.ComputeHash(stream));
            }
        }

        public static string Sha256Text(string text)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return ToHex(hash.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)));
            }
        }

        public static string Sha256Bytes(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return ToHex(hash.ComputeHash(bytes ?? new byte[0]));
            }
        }

        public static string CompositeHash(IEnumerable<SnapshotFileEntry> entries)
        {
            StringBuilder builder = new StringBuilder();
            foreach (SnapshotFileEntry entry in entries.OrderBy(
                item => item.RelativePath,
                StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(SaveLayout.NormalizeRelativePath(entry.RelativePath));
                builder.Append('\n');
                builder.Append(entry.Length);
                builder.Append('\n');
                builder.Append(entry.Sha256 ?? string.Empty);
                builder.Append('\n');
            }

            return Sha256Text(builder.ToString());
        }

        public static string ManifestHash(SnapshotManifest manifest)
        {
            if (manifest == null)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            AppendCanonical(builder, "schema", manifest.SchemaVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            AppendCanonical(builder, "id", manifest.Id);
            AppendCanonical(builder, "created", manifest.CreatedUtc);
            AppendCanonical(builder, "kind", manifest.Kind);
            AppendCanonical(builder, "label", manifest.Label);
            AppendCanonical(builder, "source", manifest.SourceProfilePath);
            AppendCanonical(builder, "purpose", manifest.Purpose);
            if (manifest.SchemaVersion >= 3)
            {
                AppendCanonical(builder, "captureBasis", manifest.CaptureBasis);
            }
            if (manifest.SchemaVersion >= 4)
            {
                AppendCanonical(
                    builder,
                    "preparationConfirmation",
                    manifest.PreparationConfirmation);
            }
            if (manifest.SchemaVersion >= 6)
            {
                AppendCanonical(builder, "profileSlotState", manifest.ProfileSlotStateCode);
                AppendCanonical(builder, "profileSlotExport", manifest.ProfileSlotExportSha256);
            }
            if (string.Equals(
                manifest.PreparationConfirmation,
                PreparationConfirmations.NativeDepartureObserved,
                StringComparison.Ordinal))
            {
                AppendCanonical(builder, "departureEvent", manifest.DepartureEventSid);
                AppendCanonical(builder, "departureRoute", manifest.DepartureRouteIndex.HasValue
                    ? manifest.DepartureRouteIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty);
                AppendCanonical(builder, "departureObserved", manifest.DepartureObservedUtc);
                AppendCanonical(builder, "departureRun", manifest.DepartureRunSha256);
                // Absent on the first departure captures; they keep their original hash.
                if (manifest.DepartureBasis != null)
                {
                    AppendCanonical(builder, "departureBasis", manifest.DepartureBasis);
                }
            }
            AppendCanonical(builder, "marker", manifest.Marker);
            AppendCanonical(builder, "state", manifest.RunStateCode);
            AppendCanonical(builder, "runLength", manifest.RunDataLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            AppendCanonical(builder, "workingLength", manifest.WorkingRunDataLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            AppendCanonical(builder, "workingBackupLength", manifest.WorkingRunBackupLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            AppendCanonical(builder, "backupCount", manifest.MatchingBackupCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            AppendCanonical(builder, "writeSkew", manifest.CoreWriteSkewMs.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            AppendCanonical(builder, "workingShape", manifest.WorkingCopyShapeMatches ? "1" : "0");
            if (manifest.SchemaVersion >= 3)
            {
                AppendCanonical(builder, "runMirrors", manifest.WorkingRunMirrorsMatch ? "1" : "0");
                AppendCanonical(builder, "profileMirrors", manifest.WorkingProfileMirrorsMatch ? "1" : "0");
            }
            AppendCanonical(builder, "complete", manifest.Complete ? "1" : "0");
            AppendCanonical(builder, "composite", manifest.CompositeSha256);

            foreach (SnapshotFileEntry entry in (manifest.Files ?? new List<SnapshotFileEntry>())
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                AppendCanonical(builder, "filePath", entry.RelativePath);
                AppendCanonical(builder, "fileLength", entry.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                AppendCanonical(builder, "fileWrite", entry.LastWriteUtc);
                AppendCanonical(builder, "fileHash", entry.Sha256);
            }

            return Sha256Text(builder.ToString());
        }

        public static string ReadMarker(string path)
        {
            byte[] bytes;
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length <= 0 || stream.Length > 512)
                {
                    return string.Empty;
                }

                bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0)
                    {
                        break;
                    }

                    read += count;
                }
            }

            return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ', '\r', '\n');
        }

        public static FileStamp GetStamp(string profilePath, string relativePath)
        {
            string fullPath = SaveLayout.CombineUnderProfile(profilePath, relativePath);
            FileInfo info = new FileInfo(fullPath);
            info.Refresh();
            if (!info.Exists)
            {
                return null;
            }

            return new FileStamp
            {
                RelativePath = SaveLayout.NormalizeRelativePath(relativePath),
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
            };
        }

        public static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Cleanup is best-effort; the original operation result is more useful.
            }
        }

        public static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Cleanup is best-effort.
            }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("X2"));
            }

            return builder.ToString();
        }

        private static void AppendCanonical(StringBuilder builder, string name, string value)
        {
            string safe = value ?? string.Empty;
            builder.Append(name);
            builder.Append(':');
            builder.Append(safe.Length);
            builder.Append(':');
            builder.Append(safe);
            builder.Append('\n');
        }
    }

    public static class RunSlotSynthesizer
    {
        private const int ChecksumOffset = 1424;
        private const int PayloadLengthOffset = 1428;
        private const int DetailOffset = 1444;
        private const int MaximumDetailBytes = 4096;
        private static readonly byte[] ExpectedIconId = { 0x01, 0x82, 0x9C, 0x01 };
        private static readonly byte[] NativeZeroFooter =
            new byte[SaveLayout.SlotEnvelopeLength];
        private static readonly byte[] NativeLegacyFooter =
        {
            0x01, 0x01, 0x01, 0x01, 0x05, 0x00, 0x00, 0x00,
            0x05, 0x02, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00,
            0x00, 0xC0, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x30, 0xC0, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };
        private static readonly uint[] Crc32CTable = BuildCrc32CTable();

        public static Result<RunSlotMetadata> Synthesize(
            string workingRunPath,
            string templateParamsPath,
            string destinationRunDataPath,
            string destinationParamsPath,
            string destinationIconPath,
            string expectedStateCode)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(workingRunPath);
                byte[] exported;
                string detail;
                string error;
                if (!TryBuildExport(
                    raw,
                    expectedStateCode,
                    out exported,
                    out detail,
                    out error))
                {
                    return Result<RunSlotMetadata>.Fail(
                        "run_slot_synthesis_unsupported",
                        error);
                }

                RunSlotMetadata template = FileTools.ReadJson<RunSlotMetadata>(
                    templateParamsPath);
                if (template == null
                    || string.IsNullOrWhiteSpace(template.title)
                    || string.IsNullOrWhiteSpace(template.subTitle))
                {
                    return Result<RunSlotMetadata>.Fail(
                        "run_slot_metadata_invalid",
                        "The existing run-slot metadata cannot provide a safe export template.");
                }

                RunSlotMetadata metadata = new RunSlotMetadata
                {
                    title = template.title,
                    subTitle = template.subTitle,
                    detail = detail,
                    userParam = template.userParam,
                    mtime = template.mtime
                };

                WriteBytesNew(destinationRunDataPath, exported);
                FileTools.WriteJsonAtomic(destinationParamsPath, metadata);
                WriteBytesNew(destinationIconPath, ExpectedIconId);

                Result<bool> verification = Verify(
                    workingRunPath,
                    destinationRunDataPath,
                    destinationParamsPath,
                    destinationIconPath,
                    expectedStateCode);
                return verification.Success
                    ? Result<RunSlotMetadata>.Ok(
                        metadata,
                        "run_slot_synthesized",
                        "A byte-verified run slot was synthesized from the working R0A state.")
                    : Result<RunSlotMetadata>.Fail(
                        verification.Code,
                        verification.Message);
            }
            catch (IOException exception)
            {
                return Result<RunSlotMetadata>.Fail("run_slot_synthesis_io_failed", exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Result<RunSlotMetadata>.Fail(
                    "run_slot_synthesis_access_denied",
                    exception.Message);
            }
            catch (Exception exception)
            {
                return Result<RunSlotMetadata>.Fail("run_slot_synthesis_failed", exception.Message);
            }
        }

        public static Result<bool> Verify(
            string workingRunPath,
            string runDataPath,
            string paramsPath,
            string iconPath,
            string expectedStateCode)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(workingRunPath);
                byte[] expected;
                string detail;
                string error;
                if (!TryBuildExport(
                    raw,
                    expectedStateCode,
                    out expected,
                    out detail,
                    out error))
                {
                    return Result<bool>.Fail("run_slot_synthesis_unsupported", error);
                }

                if (!BytesEqual(expected, File.ReadAllBytes(runDataPath)))
                {
                    return Result<bool>.Fail(
                        "run_slot_payload_mismatch",
                        "The run-slot payload is not the exact export of the captured R0A working state.");
                }

                RunSlotMetadata metadata = FileTools.ReadJson<RunSlotMetadata>(paramsPath);
                if (metadata == null
                    || !string.Equals(metadata.detail, detail, StringComparison.Ordinal)
                    || !string.Equals(
                        ExtractStateCode(metadata.detail),
                        expectedStateCode,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<bool>.Fail(
                        "run_slot_metadata_mismatch",
                        "The run-slot metadata does not identify the captured R0A working state.");
                }

                if (!BytesEqual(File.ReadAllBytes(iconPath), ExpectedIconId))
                {
                    return Result<bool>.Fail(
                        "run_slot_icon_unknown",
                        "The run-slot icon identifier does not match the observed PC save format.");
                }

                return Result<bool>.Ok(
                    true,
                    "run_slot_verified",
                    "The run slot matches the captured working state byte for byte.");
            }
            catch (Exception exception)
            {
                return Result<bool>.Fail("run_slot_verification_failed", exception.Message);
            }
        }

        public static Result<DecodedSlotPayload> DecodeNativeExport(
            string runDataPath,
            string paramsPath,
            string iconPath)
        {
            try
            {
                RunSlotMetadata metadata = FileTools.ReadJson<RunSlotMetadata>(paramsPath);
                string stateCode = ExtractStateCode(metadata == null ? null : metadata.detail);
                if (metadata == null
                    || string.IsNullOrWhiteSpace(metadata.title)
                    || string.IsNullOrWhiteSpace(metadata.subTitle)
                    || string.IsNullOrWhiteSpace(stateCode))
                {
                    return Result<DecodedSlotPayload>.Fail(
                        "native_run_metadata_invalid",
                        "The native R0A slot metadata is incomplete or malformed.");
                }

                if (!BytesEqual(File.ReadAllBytes(iconPath), ExpectedIconId))
                {
                    return Result<DecodedSlotPayload>.Fail(
                        "native_run_icon_unknown",
                        "The native R0A slot icon does not match the verified PC format.");
                }

                byte[] exported = File.ReadAllBytes(runDataPath);
                byte[] raw;
                string detail;
                string error;
                if (!TryDecodeObservedExport(
                    exported,
                    NativeZeroFooter,
                    out raw,
                    out detail,
                    out error))
                {
                    string zeroFooterError = error;
                    if (!TryDecodeObservedExport(
                        exported,
                        NativeLegacyFooter,
                        out raw,
                        out detail,
                        out error))
                    {
                        return Result<DecodedSlotPayload>.Fail(
                            "native_run_export_invalid",
                            zeroFooterError + " " + error);
                    }
                }

                if (!string.Equals(detail, metadata.detail, StringComparison.Ordinal)
                    || !string.Equals(
                        ExtractStateCode(detail),
                        stateCode,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<DecodedSlotPayload>.Fail(
                        "native_run_metadata_mismatch",
                        "The native R0A payload and metadata describe different states.");
                }

                return Result<DecodedSlotPayload>.Ok(
                    new DecodedSlotPayload
                    {
                        RawBytes = raw,
                        Detail = detail,
                        StateCode = stateCode,
                        ExportSha256 = FileTools.Sha256Bytes(exported)
                    },
                    "native_run_export_verified",
                    "The native R0A export round-trips to a verified working state.");
            }
            catch (Exception exception)
            {
                return Result<DecodedSlotPayload>.Fail(
                    "native_run_export_verification_failed",
                    exception.Message);
            }
        }

        public static Result<bool> VerifyNativeExport(
            string workingRunPath,
            string runDataPath,
            string paramsPath,
            string iconPath,
            string expectedStateCode)
        {
            Result<DecodedSlotPayload> decoded = DecodeNativeExport(
                runDataPath,
                paramsPath,
                iconPath);
            if (!decoded.Success)
            {
                return Result<bool>.Fail(decoded.Code, decoded.Message);
            }

            try
            {
                if (!string.Equals(
                        decoded.Value.StateCode,
                        expectedStateCode,
                        StringComparison.OrdinalIgnoreCase)
                    || !BytesEqual(
                        decoded.Value.RawBytes,
                        File.ReadAllBytes(workingRunPath)))
                {
                    return Result<bool>.Fail(
                        "native_run_working_mismatch",
                        "The reconstructed R0A working state does not match its native export.");
                }

                return Result<bool>.Ok(
                    true,
                    "native_run_working_verified",
                    "The reconstructed R0A state reproduces its native export exactly.");
            }
            catch (Exception exception)
            {
                return Result<bool>.Fail(
                    "native_run_working_verification_failed",
                    exception.Message);
            }
        }

        private static bool TryBuildExport(
            byte[] raw,
            string expectedStateCode,
            out byte[] exported,
            out string detail,
            out string error)
        {
            if (!TryBuildObservedExport(
                raw,
                NativeZeroFooter,
                out exported,
                out detail,
                out error))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedStateCode)
                || !string.Equals(
                    ExtractStateCode(detail),
                    expectedStateCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The working R0A run description and observed state code disagree.";
                return false;
            }
            return true;
        }

        internal static bool TryBuildObservedExport(
            byte[] raw,
            byte[] expectedFooter,
            out byte[] exported,
            out string detail,
            out string error)
        {
            exported = null;
            detail = null;
            error = null;
            if (raw == null || raw.Length <= DetailOffset + 1)
            {
                error = "The working save file is too short for the observed PC format.";
                return false;
            }

            if (expectedFooter == null
                || expectedFooter.Length != SaveLayout.SlotEnvelopeLength)
            {
                error = "The native slot footer model is invalid.";
                return false;
            }

            if (raw[0] != 0x93 || raw[1] != 0xEA)
            {
                error = "The working save header does not match the observed PC format.";
                return false;
            }

            for (int index = ChecksumOffset; index < ChecksumOffset + 4; index++)
            {
                if (raw[index] != 0)
                {
                    error = "The working checksum field is not in its observed raw form.";
                    return false;
                }
            }

            uint encodedLength = BitConverter.ToUInt32(raw, PayloadLengthOffset);
            if (encodedLength != (uint)(raw.Length - ChecksumOffset))
            {
                error = "The working payload length field is inconsistent.";
                return false;
            }

            int detailEnd = -1;
            int maximumEnd = Math.Min(raw.Length, DetailOffset + MaximumDetailBytes);
            for (int index = DetailOffset; index < maximumEnd; index++)
            {
                if (raw[index] == 0)
                {
                    detailEnd = index;
                    break;
                }
            }

            if (detailEnd <= DetailOffset)
            {
                error = "The save description is missing or too long.";
                return false;
            }

            try
            {
                detail = new UTF8Encoding(false, true).GetString(
                    raw,
                    DetailOffset,
                    detailEnd - DetailOffset);
            }
            catch (DecoderFallbackException)
            {
                error = "The save description is not valid UTF-8.";
                return false;
            }

            exported = new byte[raw.Length + SaveLayout.SlotEnvelopeLength];
            Buffer.BlockCopy(raw, 0, exported, 0, raw.Length);
            Buffer.BlockCopy(expectedFooter, 0, exported, raw.Length, expectedFooter.Length);
            exported[0] = (byte)(exported[0] & 0xFE);
            uint checksum = Crc32C(exported, PayloadLengthOffset, raw.Length - PayloadLengthOffset);
            Buffer.BlockCopy(BitConverter.GetBytes(checksum), 0, exported, ChecksumOffset, 4);
            return true;
        }

        internal static bool TryDecodeObservedExport(
            byte[] exported,
            byte[] expectedFooter,
            out byte[] raw,
            out string detail,
            out string error)
        {
            raw = null;
            detail = null;
            error = null;
            if (exported == null
                || exported.Length <= DetailOffset + SaveLayout.SlotEnvelopeLength + 1)
            {
                error = "The exported save is too short for the observed PC format.";
                return false;
            }

            if (expectedFooter == null
                || expectedFooter.Length != SaveLayout.SlotEnvelopeLength)
            {
                error = "The native slot footer model is invalid.";
                return false;
            }

            if (exported[0] != 0x92 || exported[1] != 0xEA)
            {
                error = "The exported save header does not match the observed PC format.";
                return false;
            }

            int rawLength = exported.Length - SaveLayout.SlotEnvelopeLength;
            for (int index = 0; index < expectedFooter.Length; index++)
            {
                if (exported[rawLength + index] != expectedFooter[index])
                {
                    error = "The exported save footer does not match the verified PC format.";
                    return false;
                }
            }

            uint storedChecksum = BitConverter.ToUInt32(exported, ChecksumOffset);
            uint computedChecksum = Crc32C(
                exported,
                PayloadLengthOffset,
                rawLength - PayloadLengthOffset);
            if (storedChecksum != computedChecksum)
            {
                error = "The exported save CRC32C checksum is invalid.";
                return false;
            }

            raw = new byte[rawLength];
            Buffer.BlockCopy(exported, 0, raw, 0, rawLength);
            raw[0] = (byte)(raw[0] | 0x01);
            for (int index = ChecksumOffset; index < ChecksumOffset + 4; index++)
            {
                raw[index] = 0;
            }

            byte[] rebuilt;
            if (!TryBuildObservedExport(
                raw,
                expectedFooter,
                out rebuilt,
                out detail,
                out error))
            {
                raw = null;
                return false;
            }

            if (!BytesEqual(rebuilt, exported))
            {
                raw = null;
                detail = null;
                error = "The exported save does not round-trip through the verified transform.";
                return false;
            }

            return true;
        }

        internal static string ExtractStateCode(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return null;
            }

            int close = detail.LastIndexOf(']');
            int open = close < 0 ? -1 : detail.LastIndexOf('[', close);
            if (open < 0 || close - open != 25)
            {
                return null;
            }

            string value = detail.Substring(open + 1, 24);
            return value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() : null;
        }

        private static void WriteBytesNew(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static uint Crc32C(byte[] bytes, int offset, int count)
        {
            uint value = 0xFFFFFFFF;
            int end = offset + count;
            for (int index = offset; index < end; index++)
            {
                value = Crc32CTable[(value ^ bytes[index]) & 0xFF] ^ (value >> 8);
            }

            return value ^ 0xFFFFFFFF;
        }

        private static uint[] BuildCrc32CTable()
        {
            const uint polynomial = 0x82F63B78;
            uint[] table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? polynomial ^ (value >> 1)
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        internal static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }

            for (int index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index])
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static class ProfileSlotCodec
    {
        private static readonly byte[] ExpectedIconId = { 0x14, 0x4D, 0xB7, 0x5B };
        private static readonly byte[] ExpectedFooter =
        {
            0x6F, 0xD8, 0x1B, 0xBE, 0x45, 0x27, 0x34, 0x3F,
            0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00,
            0x38, 0xF8, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x60, 0xF8, 0x02, 0x00
        };

        public static Result<DecodedSlotPayload> DecodeNativeExport(
            string profileDataPath,
            string paramsPath,
            string iconPath)
        {
            try
            {
                RunSlotMetadata metadata = FileTools.ReadJson<RunSlotMetadata>(paramsPath);
                string stateCode = RunSlotSynthesizer.ExtractStateCode(
                    metadata == null ? null : metadata.detail);
                if (metadata == null
                    || string.IsNullOrWhiteSpace(metadata.title)
                    || string.IsNullOrWhiteSpace(metadata.subTitle)
                    || string.IsNullOrWhiteSpace(stateCode))
                {
                    return Result<DecodedSlotPayload>.Fail(
                        "native_profile_metadata_invalid",
                        "The native 0P slot metadata is incomplete or malformed.");
                }

                if (!RunSlotSynthesizer.BytesEqual(
                    File.ReadAllBytes(iconPath),
                    ExpectedIconId))
                {
                    return Result<DecodedSlotPayload>.Fail(
                        "native_profile_icon_unknown",
                        "The native 0P slot icon does not match the verified PC format.");
                }

                byte[] exported = File.ReadAllBytes(profileDataPath);
                byte[] raw;
                string detail;
                string error;
                if (!RunSlotSynthesizer.TryDecodeObservedExport(
                    exported,
                    ExpectedFooter,
                    out raw,
                    out detail,
                    out error))
                {
                    return Result<DecodedSlotPayload>.Fail(
                        "native_profile_export_invalid",
                        error);
                }

                if (!string.Equals(detail, metadata.detail, StringComparison.Ordinal)
                    || !string.Equals(
                        RunSlotSynthesizer.ExtractStateCode(detail),
                        stateCode,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<DecodedSlotPayload>.Fail(
                        "native_profile_metadata_mismatch",
                        "The native 0P payload and metadata describe different states.");
                }

                return Result<DecodedSlotPayload>.Ok(
                    new DecodedSlotPayload
                    {
                        RawBytes = raw,
                        Detail = detail,
                        StateCode = stateCode,
                        ExportSha256 = FileTools.Sha256Bytes(exported)
                    },
                    "native_profile_export_verified",
                    "The native 0P export round-trips to a verified working state.");
            }
            catch (Exception exception)
            {
                return Result<DecodedSlotPayload>.Fail(
                    "native_profile_export_verification_failed",
                    exception.Message);
            }
        }

        public static Result<byte[]> BuildExport(byte[] raw)
        {
            byte[] exported;
            string detail;
            string error;
            return RunSlotSynthesizer.TryBuildObservedExport(
                raw,
                ExpectedFooter,
                out exported,
                out detail,
                out error)
                ? Result<byte[]>.Ok(
                    exported,
                    "profile_export_built",
                    "The 0P working state was exported with the verified transform.")
                : Result<byte[]>.Fail("profile_export_unsupported", error);
        }

        public static Result<bool> VerifyWorkingExportHash(
            string workingProfilePath,
            string expectedStateCode,
            string expectedExportSha256)
        {
            try
            {
                byte[] exported;
                string detail;
                string error;
                if (!RunSlotSynthesizer.TryBuildObservedExport(
                    File.ReadAllBytes(workingProfilePath),
                    ExpectedFooter,
                    out exported,
                    out detail,
                    out error))
                {
                    return Result<bool>.Fail("profile_export_unsupported", error);
                }

                if (!string.Equals(
                    RunSlotSynthesizer.ExtractStateCode(detail),
                    expectedStateCode,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return Result<bool>.Fail(
                        "profile_export_state_mismatch",
                        "The reconstructed 0P state code does not match the capture evidence.");
                }

                if (!string.Equals(
                    FileTools.Sha256Bytes(exported),
                    expectedExportSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return Result<bool>.Fail(
                        "profile_export_hash_mismatch",
                        "The reconstructed 0P export does not match the captured native slot.");
                }

                return Result<bool>.Ok(
                    true,
                    "profile_export_verified",
                    "The reconstructed 0P state reproduces the captured native slot.");
            }
            catch (Exception exception)
            {
                return Result<bool>.Fail("profile_export_verification_failed", exception.Message);
            }
        }
    }

    public static class WorkingRunStateReader
    {
        private const int SearchWindowBytes = 64 * 1024;
        private const string ObservedStateWord0 = "00800000";
        private const string ObservedStateWord1 = "00800001";

        public static Result<WorkingRunState> Inspect(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return Result<WorkingRunState>.Fail(
                        "working_state_missing",
                        "The R0A working-state file is missing.");
                }

                byte[] bytes;
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    int length = (int)Math.Min(stream.Length, SearchWindowBytes);
                    bytes = new byte[length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int count = stream.Read(bytes, read, bytes.Length - read);
                        if (count == 0)
                        {
                            break;
                        }

                        read += count;
                    }

                    if (read != bytes.Length)
                    {
                        Array.Resize(ref bytes, read);
                    }
                }

                List<WorkingRunState> candidates = new List<WorkingRunState>();
                for (int offset = 0; offset <= bytes.Length - 26; offset++)
                {
                    if (bytes[offset] != (byte)'[' || bytes[offset + 25] != (byte)']')
                    {
                        continue;
                    }

                    bool hexadecimal = true;
                    for (int index = 1; index <= 24; index++)
                    {
                        byte value = bytes[offset + index];
                        bool digit = value >= (byte)'0' && value <= (byte)'9';
                        bool upper = value >= (byte)'A' && value <= (byte)'F';
                        bool lower = value >= (byte)'a' && value <= (byte)'f';
                        if (!digit && !upper && !lower)
                        {
                            hexadecimal = false;
                            break;
                        }
                    }

                    if (!hexadecimal)
                    {
                        continue;
                    }

                    string code = Encoding.ASCII.GetString(bytes, offset + 1, 24)
                        .ToUpperInvariant();
                    string stateWord = code.Substring(8, 8);
                    uint counter;
                    if (!uint.TryParse(
                        code.Substring(16, 8),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out counter))
                    {
                        continue;
                    }

                    string signal = string.Equals(
                            stateWord,
                            ObservedStateWord1,
                            StringComparison.Ordinal)
                        ? WorkingRunStateSignals.ObservedWord1
                        : string.Equals(
                                stateWord,
                                ObservedStateWord0,
                                StringComparison.Ordinal)
                            ? WorkingRunStateSignals.ObservedWord0
                            : WorkingRunStateSignals.Unknown;
                    candidates.Add(new WorkingRunState
                    {
                        Code = code,
                        IdentityWord = code.Substring(0, 8),
                        StateWord = stateWord,
                        Counter = counter,
                        Offset = offset,
                        StateSignal = signal
                    });
                }

                List<WorkingRunState> observed = candidates.Where(item => !string.Equals(
                    item.StateSignal,
                    WorkingRunStateSignals.Unknown,
                    StringComparison.Ordinal)).ToList();
                if (observed.Count == 1)
                {
                    return Result<WorkingRunState>.Ok(
                        observed[0],
                        "working_state_observed",
                        "The R0A working-state word was observed.");
                }

                if (observed.Count > 1 || candidates.Count > 1)
                {
                    return Result<WorkingRunState>.Fail(
                        "working_state_ambiguous",
                        "More than one R0A working-state code was found.");
                }

                if (candidates.Count == 1)
                {
                    return Result<WorkingRunState>.Ok(
                        candidates[0],
                        "working_state_word_unrecognized",
                        "The R0A state code uses an unobserved state word.");
                }

                return Result<WorkingRunState>.Fail(
                    "working_state_code_missing",
                    "No R0A working-state code was found in the observed header window.");
            }
            catch (IOException exception)
            {
                return Result<WorkingRunState>.Fail("working_state_busy", exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Result<WorkingRunState>.Fail("working_state_access_denied", exception.Message);
            }
            catch (Exception exception)
            {
                return Result<WorkingRunState>.Fail("working_state_probe_failed", exception.Message);
            }
        }
    }

    public static class RunStateProbe
    {
        private static readonly TimeSpan BackupEvidenceWindow = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaximumCoreWriteSkew = TimeSpan.FromSeconds(30);

        private sealed class BackupEvidence
        {
            public int MatchingCount { get; set; }
            public string Signature { get; set; }
        }

        public static Result<RunStateDescriptor> Inspect(string profilePath, bool allowIncomplete)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath))
                {
                    return Result<RunStateDescriptor>.Fail(
                        "profile_missing",
                        "The selected save profile does not exist.");
                }

                RunStateDescriptor descriptor = new RunStateDescriptor
                {
                    ProfilePath = Path.GetFullPath(profilePath)
                };

                foreach (string relativePath in SaveLayout.RequiredFiles)
                {
                    FileStamp stamp = FileTools.GetStamp(profilePath, relativePath);
                    if (stamp != null)
                    {
                        descriptor.Files.Add(stamp);
                    }
                }

                descriptor.Files = descriptor.Files
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string relativePath in SaveLayout.NativeExportSourceFiles)
                {
                    FileStamp stamp = FileTools.GetStamp(profilePath, relativePath);
                    if (stamp != null)
                    {
                        descriptor.NativeExportFiles.Add(stamp);
                    }
                }

                descriptor.NativeExportFiles = descriptor.NativeExportFiles
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                bool hasAllFiles = SaveLayout.RequiredFiles.All(
                    path => descriptor.Files.Any(
                        stamp => string.Equals(
                            stamp.RelativePath,
                            path,
                            StringComparison.OrdinalIgnoreCase)));

                FileStamp markerStamp = descriptor.Files.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.MarkerPath,
                        StringComparison.OrdinalIgnoreCase));
                FileStamp dataStamp = descriptor.Files.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.RunDataPath,
                        StringComparison.OrdinalIgnoreCase));
                FileStamp paramsStamp = descriptor.Files.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.RunParamsPath,
                        StringComparison.OrdinalIgnoreCase));
                FileStamp workingRunStamp = descriptor.Files.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.GameRunPath,
                        StringComparison.OrdinalIgnoreCase));
                FileStamp workingRunBackupStamp = descriptor.Files.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.GameRunBackupPath,
                        StringComparison.OrdinalIgnoreCase));
                FileStamp workingProfileStamp = descriptor.Files.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.GameProfilePath,
                        StringComparison.OrdinalIgnoreCase));
                FileStamp workingProfileBackupStamp = descriptor.Files.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.GameProfileBackupPath,
                        StringComparison.OrdinalIgnoreCase));
                FileStamp profileDataStamp = descriptor.NativeExportFiles.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.ProfileDataPath,
                        StringComparison.OrdinalIgnoreCase));
                FileStamp profileParamsStamp = descriptor.NativeExportFiles.FirstOrDefault(
                    stamp => string.Equals(
                        stamp.RelativePath,
                        SaveLayout.ProfileParamsPath,
                        StringComparison.OrdinalIgnoreCase));

                if (markerStamp != null)
                {
                    descriptor.Marker = FileTools.ReadMarker(
                        SaveLayout.CombineUnderProfile(profilePath, SaveLayout.MarkerPath));
                }

                descriptor.Complete = hasAllFiles
                    && descriptor.Files.All(stamp => stamp.Length > 0)
                    && markerStamp != null
                    && markerStamp.Length <= 512
                    && dataStamp != null
                    && dataStamp.Length > 1024
                    && !string.IsNullOrWhiteSpace(descriptor.Marker);

                if (dataStamp != null)
                {
                    descriptor.RunDataLength = dataStamp.Length;
                }

                if (workingRunStamp != null)
                {
                    descriptor.WorkingRunDataLength = workingRunStamp.Length;
                }

                if (workingRunBackupStamp != null)
                {
                    descriptor.WorkingRunBackupLength = workingRunBackupStamp.Length;
                }

                if (paramsStamp != null)
                {
                    try
                    {
                        RunSlotMetadata metadata = FileTools.ReadJson<RunSlotMetadata>(
                            SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunParamsPath));
                        descriptor.RunStateCode = ExtractStateCode(
                            metadata == null ? null : metadata.detail);
                    }
                    catch
                    {
                        descriptor.RunStateCode = null;
                    }
                }

                descriptor.EmptyRunSlot = string.Equals(
                    descriptor.RunStateCode,
                    SaveLayout.EmptyRunStateCode,
                    StringComparison.OrdinalIgnoreCase);
                WorkingRunState observedWorkingState = null;
                if (workingRunStamp != null)
                {
                    Result<WorkingRunState> workingState = WorkingRunStateReader.Inspect(
                        SaveLayout.CombineUnderProfile(profilePath, SaveLayout.GameRunPath));
                    if (workingState.Success)
                    {
                        observedWorkingState = workingState.Value;
                        descriptor.WorkingRunStateCode = workingState.Value.Code;
                        descriptor.WorkingRunStateWord = workingState.Value.StateWord;
                        descriptor.WorkingRunCounter = workingState.Value.Counter;
                        descriptor.WorkingRunStateOffset = workingState.Value.Offset;
                        descriptor.WorkingRunStateSignal = workingState.Value.StateSignal;
                    }
                }

                int workingFileCount = new[]
                {
                    workingRunStamp,
                    workingRunBackupStamp,
                    workingProfileStamp,
                    workingProfileBackupStamp
                }.Count(item => item != null);
                bool noWorkingEnvelope = workingFileCount == 0;
                bool completeNativeSource = SaveLayout.NativeExportSourceFiles.All(
                    path => descriptor.NativeExportFiles.Any(
                        stamp => string.Equals(
                            stamp.RelativePath,
                            path,
                            StringComparison.OrdinalIgnoreCase)
                            && stamp.Length > 0));
                Result<DecodedSlotPayload> nativeRun = null;
                Result<DecodedSlotPayload> nativeProfile = null;
                if (noWorkingEnvelope && completeNativeSource)
                {
                    nativeRun = RunSlotSynthesizer.DecodeNativeExport(
                        SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunDataPath),
                        SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunParamsPath),
                        SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunIconPath));
                    nativeProfile = ProfileSlotCodec.DecodeNativeExport(
                        SaveLayout.CombineUnderProfile(profilePath, SaveLayout.ProfileDataPath),
                        SaveLayout.CombineUnderProfile(profilePath, SaveLayout.ProfileParamsPath),
                        SaveLayout.CombineUnderProfile(profilePath, SaveLayout.ProfileIconPath));

                    if (nativeRun.Success)
                    {
                        descriptor.WorkingRunDataLength = nativeRun.Value.RawBytes.Length;
                        descriptor.WorkingRunBackupLength = nativeRun.Value.RawBytes.Length;
                        Result<WorkingRunState> exportedState = WorkingRunStateReader.Inspect(
                            SaveLayout.CombineUnderProfile(profilePath, SaveLayout.RunDataPath));
                        if (exportedState.Success)
                        {
                            observedWorkingState = exportedState.Value;
                            descriptor.WorkingRunStateCode = exportedState.Value.Code;
                            descriptor.WorkingRunStateWord = exportedState.Value.StateWord;
                            descriptor.WorkingRunCounter = exportedState.Value.Counter;
                            descriptor.WorkingRunStateOffset = exportedState.Value.Offset;
                            descriptor.WorkingRunStateSignal = exportedState.Value.StateSignal;
                        }
                    }

                    if (nativeProfile.Success)
                    {
                        descriptor.ProfileSlotStateCode = nativeProfile.Value.StateCode;
                        descriptor.ProfileSlotExportSha256 = nativeProfile.Value.ExportSha256;
                    }
                }

                descriptor.NativeExportWriteSkewMs = CoreWriteSkewMs(
                    dataStamp,
                    paramsStamp,
                    markerStamp,
                    profileDataStamp,
                    profileParamsStamp);
                descriptor.NativeExportCaptureStatus = NativeExportCaptureStatus(
                    workingFileCount,
                    completeNativeSource,
                    nativeRun,
                    nativeProfile,
                    descriptor);
                descriptor.NativeExportCaptureReady = string.Equals(
                    descriptor.NativeExportCaptureStatus,
                    "native_export_capture_ready",
                    StringComparison.Ordinal);

                BackupEvidence backupEvidence = InspectBackupEvidence(
                    profilePath,
                    workingRunStamp,
                    observedWorkingState);
                descriptor.MatchingBackupCount = backupEvidence.MatchingCount;
                descriptor.BackupEvidenceSignature = backupEvidence.Signature;
                descriptor.CoreWriteSkewMs = CoreWriteSkewMs(
                    markerStamp,
                    workingRunStamp,
                    workingRunBackupStamp,
                    workingProfileStamp,
                    workingProfileBackupStamp);
                descriptor.WorkingCopyShapeMatches = RawShapeMatches(
                        dataStamp,
                        workingRunStamp)
                    && RawShapeMatches(dataStamp, workingRunBackupStamp);
                descriptor.WorkingRunMirrorsMatch = FilesMatch(
                    profilePath,
                    workingRunStamp,
                    workingRunBackupStamp);
                descriptor.WorkingProfileMirrorsMatch = FilesMatch(
                    profilePath,
                    workingProfileStamp,
                    workingProfileBackupStamp);
                if (string.IsNullOrWhiteSpace(descriptor.WorkingRunStateSignal))
                {
                    descriptor.WorkingRunStateSignal = WorkingRunStateSignals.Unknown;
                }

                descriptor.WorkingCaptureReady = descriptor.Complete
                    && descriptor.WorkingRunMirrorsMatch
                    && descriptor.WorkingProfileMirrorsMatch
                    && WorkingRunStateSignals.IsRecognized(descriptor.WorkingRunStateSignal)
                    && descriptor.CoreWriteSkewMs <= (long)MaximumCoreWriteSkew.TotalMilliseconds;
                descriptor.ManualCaptureReady = descriptor.WorkingCaptureReady
                    || descriptor.NativeExportCaptureReady;
                descriptor.ManualCaptureBasis = descriptor.WorkingCaptureReady
                    ? SnapshotCaptureBases.HotSynthesizedState
                    : descriptor.NativeExportCaptureReady
                        ? SnapshotCaptureBases.NativeExportedState
                        : null;
                descriptor.ManualCaptureStatus = ManualCaptureStatus(descriptor);
                descriptor.CommittedCaptureReady = descriptor.NativeExportCaptureReady
                    || (descriptor.WorkingCaptureReady
                    && !string.IsNullOrWhiteSpace(descriptor.RunStateCode)
                    && string.Equals(
                        descriptor.RunStateCode,
                        descriptor.WorkingRunStateCode,
                        StringComparison.OrdinalIgnoreCase)
                    && descriptor.RunDataLength
                        == descriptor.WorkingRunDataLength + SaveLayout.SlotEnvelopeLength);
                descriptor.CommittedCaptureStatus = CommittedCaptureStatus(descriptor);

                descriptor.QuickSignature = BuildQuickSignature(
                    descriptor.NativeExportCaptureReady
                        ? descriptor.NativeExportFiles
                        : descriptor.Files,
                    descriptor.BackupEvidenceSignature,
                    descriptor.WorkingRunStateCode);

                if (!allowIncomplete && !descriptor.Complete)
                {
                    return Result<RunStateDescriptor>.Fail(
                        "run_incomplete",
                        "No complete No Return run state is currently available.");
                }

                return Result<RunStateDescriptor>.Ok(
                    descriptor,
                    descriptor.Complete ? "run_complete" : "run_partial",
                    descriptor.Complete
                        ? "A coherent run state is available."
                        : "Only a partial run state is available.");
            }
            catch (IOException exception)
            {
                return Result<RunStateDescriptor>.Fail("run_busy", exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Result<RunStateDescriptor>.Fail("run_access_denied", exception.Message);
            }
            catch (Exception exception)
            {
                return Result<RunStateDescriptor>.Fail("run_probe_failed", exception.Message);
            }
        }

        private static string ExtractStateCode(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return null;
            }

            int close = detail.LastIndexOf(']');
            int open = close < 0 ? -1 : detail.LastIndexOf('[', close);
            if (open < 0 || close <= open + 1)
            {
                return null;
            }

            string value = detail.Substring(open + 1, close - open - 1).Trim();
            return IsHexStateCode(value) ? value.ToUpperInvariant() : null;
        }

        private static bool IsHexStateCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 24)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                if (!Uri.IsHexDigit(value[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static BackupEvidence InspectBackupEvidence(
            string profilePath,
            FileStamp workingRunStamp,
            WorkingRunState workingState)
        {
            BackupEvidence evidence = new BackupEvidence
            {
                MatchingCount = 0,
                Signature = string.Empty
            };
            if (workingRunStamp == null)
            {
                return evidence;
            }

            string backupRoot = Path.Combine(profilePath, "savedata", "backup");
            if (!Directory.Exists(backupRoot))
            {
                return evidence;
            }

            DateTime dataWrite = new DateTime(
                workingRunStamp.LastWriteUtcTicks,
                DateTimeKind.Utc);
            StringBuilder signature = new StringBuilder();
            foreach (string path in Directory.GetFiles(
                backupRoot,
                "USR-DATA.R0A_bak*",
                SearchOption.TopDirectoryOnly).OrderBy(
                    item => item,
                    StringComparer.OrdinalIgnoreCase))
            {
                FileInfo info = new FileInfo(path);
                info.Refresh();
                signature.Append(Path.GetFileName(path));
                signature.Append(':');
                signature.Append(info.Length);
                signature.Append(':');
                signature.Append(info.LastWriteTimeUtc.Ticks);
                Result<WorkingRunState> backupState = WorkingRunStateReader.Inspect(path);
                signature.Append(':');
                signature.Append(backupState.Success ? backupState.Value.Code : "unknown");
                signature.Append('|');
                bool lengthMatches = workingRunStamp.Length == info.Length;
                if (lengthMatches
                    && Math.Abs((info.LastWriteTimeUtc - dataWrite).TotalMilliseconds)
                        <= BackupEvidenceWindow.TotalMilliseconds
                    && workingState != null
                    && backupState.Success
                    && string.Equals(
                        backupState.Value.IdentityWord,
                        workingState.IdentityWord,
                        StringComparison.Ordinal)
                    && string.Equals(
                        backupState.Value.StateWord,
                        workingState.StateWord,
                        StringComparison.Ordinal)
                    && backupState.Value.Counter <= workingState.Counter)
                {
                    evidence.MatchingCount++;
                }
            }

            evidence.Signature = signature.ToString();
            return evidence;
        }

        private static bool FilesMatch(
            string profilePath,
            FileStamp first,
            FileStamp second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }

            string firstPath = SaveLayout.CombineUnderProfile(
                profilePath,
                first.RelativePath);
            string secondPath = SaveLayout.CombineUnderProfile(
                profilePath,
                second.RelativePath);
            return string.Equals(
                FileTools.Sha256(firstPath, true),
                FileTools.Sha256(secondPath, true),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool RawShapeMatches(FileStamp slot, FileStamp raw)
        {
            return slot != null && raw != null && RawShapeMatches(slot.Length, raw.Length);
        }

        private static bool RawShapeMatches(long slotLength, long rawLength)
        {
            return rawLength == slotLength
                || rawLength + SaveLayout.SlotEnvelopeLength == slotLength;
        }

        private static long CoreWriteSkewMs(params FileStamp[] stamps)
        {
            if (stamps == null || stamps.Any(item => item == null))
            {
                return long.MaxValue;
            }

            long minimum = stamps.Min(item => item.LastWriteUtcTicks);
            long maximum = stamps.Max(item => item.LastWriteUtcTicks);
            return (maximum - minimum) / TimeSpan.TicksPerMillisecond;
        }

        private static string ManualCaptureStatus(RunStateDescriptor descriptor)
        {
            if (descriptor.WorkingCaptureReady)
            {
                return "manual_capture_ready";
            }

            if (descriptor.NativeExportCaptureReady)
            {
                return "native_export_capture_ready";
            }

            int workingFileCount = new[]
            {
                descriptor.Files.Any(item => string.Equals(
                    item.RelativePath,
                    SaveLayout.GameRunPath,
                    StringComparison.OrdinalIgnoreCase)),
                descriptor.Files.Any(item => string.Equals(
                    item.RelativePath,
                    SaveLayout.GameRunBackupPath,
                    StringComparison.OrdinalIgnoreCase)),
                descriptor.Files.Any(item => string.Equals(
                    item.RelativePath,
                    SaveLayout.GameProfilePath,
                    StringComparison.OrdinalIgnoreCase)),
                descriptor.Files.Any(item => string.Equals(
                    item.RelativePath,
                    SaveLayout.GameProfileBackupPath,
                    StringComparison.OrdinalIgnoreCase))
            }.Count(value => value);

            if (workingFileCount == 0)
            {
                return descriptor.NativeExportCaptureStatus ?? "run_incomplete";
            }

            if (workingFileCount < 4)
            {
                return "run_working_envelope_partial";
            }

            if (!descriptor.Complete)
            {
                return "run_incomplete";
            }

            if (!descriptor.WorkingRunMirrorsMatch)
            {
                return "run_working_mirror_mismatch";
            }

            if (!descriptor.WorkingProfileMirrorsMatch)
            {
                return "run_profile_mirror_mismatch";
            }

            if (!WorkingRunStateSignals.IsRecognized(descriptor.WorkingRunStateSignal))
            {
                return "run_state_word_unrecognized";
            }

            if (descriptor.CoreWriteSkewMs > (long)MaximumCoreWriteSkew.TotalMilliseconds)
            {
                return "run_generation_mixed";
            }

            return "manual_capture_ready";
        }

        private static string NativeExportCaptureStatus(
            int workingFileCount,
            bool completeNativeSource,
            Result<DecodedSlotPayload> nativeRun,
            Result<DecodedSlotPayload> nativeProfile,
            RunStateDescriptor descriptor)
        {
            if (workingFileCount != 0)
            {
                return workingFileCount == 4
                    ? "native_export_not_applicable"
                    : "run_working_envelope_partial";
            }

            if (!completeNativeSource)
            {
                return "run_incomplete";
            }

            if (nativeRun == null || !nativeRun.Success)
            {
                return nativeRun == null ? "native_run_export_invalid" : nativeRun.Code;
            }

            if (nativeProfile == null || !nativeProfile.Success)
            {
                return nativeProfile == null
                    ? "native_profile_export_invalid"
                    : nativeProfile.Code;
            }

            if (!string.Equals(
                descriptor.RunStateCode,
                nativeRun.Value.StateCode,
                StringComparison.OrdinalIgnoreCase))
            {
                return "native_run_metadata_mismatch";
            }

            if (!WorkingRunStateSignals.IsRecognized(descriptor.WorkingRunStateSignal))
            {
                return "run_state_word_unrecognized";
            }

            if (descriptor.NativeExportWriteSkewMs
                > (long)MaximumCoreWriteSkew.TotalMilliseconds)
            {
                return "run_generation_mixed";
            }

            return "native_export_capture_ready";
        }

        private static string CommittedCaptureStatus(RunStateDescriptor descriptor)
        {
            if (!descriptor.ManualCaptureReady)
            {
                return descriptor.ManualCaptureStatus;
            }

            if (string.IsNullOrWhiteSpace(descriptor.RunStateCode))
            {
                return "run_slot_state_missing";
            }

            if (!string.Equals(
                descriptor.RunStateCode,
                descriptor.WorkingRunStateCode,
                StringComparison.OrdinalIgnoreCase))
            {
                return "run_slot_state_mismatch";
            }

            if (descriptor.RunDataLength
                != descriptor.WorkingRunDataLength + SaveLayout.SlotEnvelopeLength)
            {
                return "run_slot_not_committed";
            }

            return "committed_capture_ready";
        }

        private static string BuildQuickSignature(
            IEnumerable<FileStamp> stamps,
            string backupEvidenceSignature,
            string workingRunStateCode)
        {
            StringBuilder builder = new StringBuilder();
            foreach (FileStamp stamp in stamps.OrderBy(
                item => item.RelativePath,
                StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(stamp.RelativePath);
                builder.Append(':');
                builder.Append(stamp.Length);
                builder.Append(':');
                builder.Append(stamp.LastWriteUtcTicks);
                builder.Append('|');
            }

            builder.Append("backups:");
            builder.Append(backupEvidenceSignature ?? string.Empty);
            builder.Append("|working-state:");
            builder.Append(workingRunStateCode ?? string.Empty);

            return builder.ToString();
        }
    }
}
