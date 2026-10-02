using NoReturnGuardian.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NoReturnGuardian
{
    /// <summary>
    /// 把守护状态和快照库译成界面读的字段。这里只决定“玩家看到什么”；能不能做由 MainForm 的门控决定，
    /// 界面发来的每条命令都会在执行前重新核对。
    /// </summary>
    internal static class GuardianView
    {
        internal sealed class StatusInput
        {
            public bool ProfileFound;
            public bool GameRunning;
            public bool SuspectedRunEnded;
            public bool CaptureReady;
            public bool HasProtected;
            public int Restorable;
            public bool NativeAvailable;
            public bool PendingRestore;
            public bool WarmActive;
        }

        internal sealed class Status
        {
            public string Mode;
            public string Headline;
            public string Detail;
            public string Blocked;
        }

        /// <summary>主窗口此刻的一切：守护状态、快照库、设置和各个控制器的状态。</summary>
        internal sealed class StateInput
        {
            public MonitorStatus Monitor;
            public IList<SnapshotRecord> Records;
            public GuardianSettings Settings;
            public bool PendingRestore;
            public bool WarmActive;
            public bool Cleaning;
            public bool NativeAvailable;
            public bool NativeRunning;
            public bool AutoProtect;
            public bool StartupEnabled;
            public string UndoSnapshotId;
            public string FocusId;
            public int FocusNonce;
            /// <summary>注册成功的游戏内快捷键；没注册的为空。</summary>
            public string RecoverHotkey;
            public string RestartHotkey;
            public Func<string, bool> HasDeparture;
            /// <summary>GitHub 上比当前更新的正式版；没有或玩家关了检查更新时为空。</summary>
            public Version UpdateAvailable;
        }

        /// <summary>
        /// 推给界面的完整状态（src/NoReturnGuardian.Web/src/types.ts 的 ViewState）。按钮能不能按只是提示，
        /// 每条命令执行前宿主都会重新核对。
        /// </summary>
        internal static Dictionary<string, object> State(StateInput input)
        {
            MonitorStatus status = input.Monitor;
            GuardianSettings settings = input.Settings;
            IList<SnapshotRecord> records = input.Records;
            bool gameRunning = status != null && status.GameRunning;
            bool profileFound = !string.IsNullOrWhiteSpace(settings.ProfilePath);
            int restorable = records.Count(SnapshotPolicy.IsRedeployablePreparation);
            bool hasProtected = status != null && !string.IsNullOrWhiteSpace(status.LastProtectedSnapshotId);
            Status described = DescribeStatus(new StatusInput
            {
                ProfileFound = profileFound,
                GameRunning = gameRunning,
                SuspectedRunEnded = status != null && status.SuspectedRunEnded,
                CaptureReady = status != null && status.ManualCaptureReady,
                HasProtected = hasProtected,
                Restorable = restorable,
                NativeAvailable = input.NativeAvailable,
                PendingRestore = input.PendingRestore,
                WarmActive = input.WarmActive
            });

            DateTime today = DateTime.Now;
            SnapshotRecord protectedRecord = hasProtected
                ? records.FirstOrDefault(record => record.Manifest.Id == status.LastProtectedSnapshotId)
                : null;
            bool blockedRestore = input.PendingRestore || input.Cleaning || !profileFound || input.WarmActive;
            bool native = gameRunning && input.NativeAvailable;
            var snapshots = records.Select(record =>
            {
                DateTime local = record.CreatedUtc.ToLocalTime();
                bool ready = SnapshotPolicy.IsConfirmedPreparation(record);
                bool departure = SnapshotPolicy.IsRedeployablePreparation(record) && input.HasDeparture(record.Manifest.Id);
                string restore = null;
                if (ready && !blockedRestore)
                {
                    if (native && record.Manifest.SchemaVersion == 5)
                    {
                        restore = "native";
                    }
                    else if (!gameRunning)
                    {
                        restore = "file";
                    }
                }

                return Map(
                    "id", record.Manifest.Id,
                    "time", TimeOf(local),
                    "stamp", Stamp(local),
                    "day", DayLabel(local, today),
                    "kind", KindOf(record),
                    "state", StateOf(record),
                    "restore", restore,
                    // 重开战斗只在游戏运行时有意义：从结算页、菜单、战斗中或刚恢复出的兵营出发。
                    "restart", departure && native && !blockedRestore && record.Manifest.SchemaVersion == 5,
                    "departure", departure,
                    "preRestore", string.Equals(record.Manifest.Kind, SnapshotKinds.PreRestore, StringComparison.OrdinalIgnoreCase));
            }).ToList();

            long bytes = records.Where(record => record.Manifest.Files != null)
                .Sum(record => record.Manifest.Files.Sum(file => Math.Max(0, file.Length)));
            return Map(
                "mode", described.Mode,
                "headline", described.Headline,
                "detail", described.Detail,
                "gameRunning", gameRunning,
                "lastProtected", protectedRecord == null ? null : Map(
                    "time", TimeOf(protectedRecord.CreatedUtc.ToLocalTime()),
                    "day", DayLabel(protectedRecord.CreatedUtc.ToLocalTime(), today)),
                "hotkeys", Map(
                    "recover", native ? input.RecoverHotkey : null,
                    "restart", native ? input.RestartHotkey : null),
                "profile", Map(
                    "found", profileFound,
                    "name", profileFound ? Path.GetFileName(settings.ProfilePath) : ""),
                "snapshots", snapshots,
                "focus", Map("id", input.FocusId, "nonce", input.FocusNonce),
                "actions", Map(
                    "protect", !input.PendingRestore && profileFound,
                    "undo", !gameRunning && !input.PendingRestore && !input.Cleaning && !string.IsNullOrWhiteSpace(input.UndoSnapshotId),
                    "launch", !gameRunning && !input.PendingRestore,
                    "cleanup", !input.PendingRestore && !input.WarmActive && !input.Cleaning && profileFound,
                    "remove", !input.PendingRestore && !input.Cleaning,
                    "warm", input.WarmActive),
                "blocked", described.Blocked,
                "settings", Map(
                    "autoProtect", input.AutoProtect,
                    "autoProtectAvailable", input.NativeAvailable,
                    "startup", input.StartupEnabled,
                    "autoCleanup", settings.AutoCleanup,
                    "showUndo", settings.ShowUndoPoints,
                    "panelTransparency", settings.PanelTransparency,
                    "checkUpdates", settings.CheckForUpdates,
                    "profilePath", settings.ProfilePath ?? ""),
                "version", Map(
                    "current", ReleaseFeed.Display(UpdateCheck.Current),
                    "available", input.UpdateAvailable == null ? null : ReleaseFeed.Display(input.UpdateAvailable)),
                "library", Map(
                    "count", records.Count,
                    "size", FormatBytes(bytes),
                    "preparations", restorable,
                    "scenes", records.Count(record => StateOf(record) == "scene")),
                "busy", input.Cleaning,
                "working", input.NativeRunning);
        }

        /// <summary>
        /// 状态字与副行。措辞保持证据边界：游戏退出后的终局永远是“疑似结束”，
        /// 非战斗状态也不直接说成“在兵营”。
        /// </summary>
        internal static Status DescribeStatus(StatusInput input)
        {
            string game = input.GameRunning ? "游戏运行中" : "游戏未运行";
            string count = input.Restorable > 0 ? input.Restorable + " 份可恢复" : null;
            if (input.PendingRestore || input.WarmActive)
            {
                return new Status
                {
                    Mode = "alert",
                    Headline = "恢复未完成",
                    Detail = input.WarmActive ? "上次的暖恢复还没有结束" : input.GameRunning ? "退出游戏后重新打开守护器" : "需要先回滚到恢复之前",
                    Blocked = input.GameRunning && input.PendingRestore ? "完成回滚前不要继续游玩" : null
                };
            }

            if (!input.ProfileFound)
            {
                return new Status { Mode = "idle", Headline = "待机", Detail = "没有找到游戏存档" };
            }

            if (input.SuspectedRunEnded)
            {
                return new Status
                {
                    Mode = "alert",
                    Headline = "征途疑似结束",
                    Detail = !input.HasProtected
                        ? "没有可恢复的战备"
                        : !input.GameRunning
                            ? "选一份战备恢复"
                            : input.NativeAvailable ? "可以回兵营或重开这场战斗" : "完整退出游戏后可恢复"
                };
            }

            if (input.CaptureReady)
            {
                return new Status
                {
                    Mode = "guarding",
                    Headline = input.HasProtected ? "守护中" : "可以保护",
                    Detail = game + " · " + (input.HasProtected ? count ?? "已保护" : "确认在兵营后保护")
                };
            }

            return new Status
            {
                Mode = input.GameRunning ? "observing" : "idle",
                Headline = input.GameRunning ? "观察中" : "待机",
                Detail = game + (count != null ? " · " + count : input.GameRunning ? " · 进入兵营后可保护" : string.Empty)
            };
        }

        internal static string DayLabel(DateTime local, DateTime today)
        {
            DateTime day = local.Date;
            if (day == today.Date)
            {
                return "今天";
            }

            if (day == today.Date.AddDays(-1))
            {
                return "昨天";
            }

            return day.Year == today.Year
                ? day.Month + "月" + day.Day + "日"
                : day.Year + "年" + day.Month + "月" + day.Day + "日";
        }

        internal static string Stamp(DateTime local)
        {
            return local.Year + "年" + local.Month + "月" + local.Day + "日 "
                + local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// ready 可恢复 / restart 只能重开它的战斗（出发后才存下的旧版自动保存）/ scene 恢复前现场 /
        /// locked 旧版或误捕 / corrupt 清单损坏。
        /// </summary>
        internal static string StateOf(SnapshotRecord record)
        {
            if (record == null || !record.StructurallyValid || record.Manifest == null)
            {
                return "corrupt";
            }

            if (SnapshotPolicy.IsConfirmedPreparation(record))
            {
                return "ready";
            }

            if (SnapshotPolicy.IsPostDepartureSave(record))
            {
                return "restart";
            }

            if (record.Manifest.SchemaVersion >= 2 && string.Equals(
                record.Manifest.Kind, SnapshotKinds.PreRestore, StringComparison.OrdinalIgnoreCase))
            {
                return "scene";
            }

            return "locked";
        }

        internal static string KindOf(SnapshotRecord record)
        {
            SnapshotManifest manifest = record == null ? null : record.Manifest;
            if (manifest == null || !record.StructurallyValid)
            {
                return "损坏";
            }

            if (manifest.SchemaVersion < 2)
            {
                return "旧版";
            }

            if (string.Equals(manifest.Kind, SnapshotKinds.Manual, StringComparison.OrdinalIgnoreCase))
            {
                return "手动";
            }

            if (string.Equals(manifest.Kind, SnapshotKinds.Departure, StringComparison.OrdinalIgnoreCase))
            {
                return manifest.DepartureBasis == null ? "出发后" : "出发前";
            }

            if (string.Equals(manifest.Kind, SnapshotKinds.PreRestore, StringComparison.OrdinalIgnoreCase))
            {
                return "恢复前";
            }

            return manifest.SchemaVersion >= 3 ? "自动" : "旧版";
        }

        internal static string TimeOf(DateTime local)
        {
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30)
            {
                return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            }

            return Math.Max(0.1, bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>
        /// 操作结果的玩家可读说明。Core 的原话是给开发者的英文，不上界面，由调用方记进诊断记录。
        /// </summary>
        internal static string FriendlyMessage(string code, string detail)
        {
            switch (code)
            {
                case "snapshot_created":
                case "hot_snapshot_created":
                case "native_export_snapshot_created": return "战备已保护";
                case "snapshot_unchanged": return "这份战备已经保护过了";
                case "automatic_capture_disabled":
                case "preparation_confirmation_required": return "需要确认在兵营，本次没有保存";
                case "run_incomplete": return "没有找到完整的征途存档，本次没有保存";
                case "run_working_mirror_mismatch":
                case "run_profile_mirror_mismatch":
                case "run_generation_mixed":
                case "run_working_envelope_partial":
                case "source_changed":
                case "source_changed_after_hash": return "游戏正在写存档，稍等几秒再试";
                case "run_not_stable": return "存档刚刚变化，稍等几秒再试";
                case "run_state_word_1_rejected": return "请确认在兵营后再保护";
                case "run_state_word_unrecognized": return "无法确认当前存档状态，本次没有保存";
                case "native_run_export_invalid":
                case "native_run_export_verification_failed":
                case "native_profile_export_invalid":
                case "native_profile_export_verification_failed":
                case "native_run_metadata_invalid":
                case "native_run_metadata_mismatch":
                case "native_profile_metadata_invalid":
                case "native_profile_metadata_mismatch":
                case "native_run_icon_unknown":
                case "native_profile_icon_unknown":
                case "run_slot_synthesis_unsupported":
                case "run_slot_metadata_invalid":
                case "run_slot_icon_unknown": return "存档没有通过校验，本次没有保存";
                case "run_slot_payload_mismatch":
                case "run_slot_metadata_mismatch":
                case "snapshot_hash_mismatch":
                case "snapshot_manifest_hash_mismatch":
                case "snapshot_invalid": return "这份快照没有通过校验，不能恢复";
                case "snapshot_unconfirmed": return "这份快照不能恢复";
                case "snapshot_profile_mismatch": return "这份快照属于另一个存档账号";
                case "game_running": return "游戏还在运行，请先完整退出游戏";
                case "restore_written_verified": return "战备已写回";
                case "undo_complete": return "已撤销最近一次恢复";
                case "restore_failed_rolled_back": return "恢复失败，存档已自动还原";
                case "undo_capture_failed": return "没能先保存当前现场，所以没有恢复";
                case "snapshot_deleted": return "已删除";
                case "snapshot_delete_blocked_restore_pending":
                case "snapshot_cleanup_blocked_restore_pending": return "上次恢复还没完成，暂时不能删除";
                case "snapshot_cleanup_plan_changed": return "快照列表刚有变化，这次没有删除";
                case "snapshot_cleanup_partial": return "部分快照没能删除，可以稍后再试";
                case "snapshot_cleanup_failed": return "清理没有完成，游戏存档未改动";
                case "snapshot_not_found": return "这份快照已经不在了";
                case "snapshot_incomplete":
                case "snapshot_file_missing":
                case "snapshot_length_mismatch": return "这份快照没有通过校验，不能恢复";
                case "settings_save_failed": return "设置没有保存成功";
                default:
                    return code != null && (code.EndsWith("_io_failed", StringComparison.Ordinal)
                        || code.EndsWith("_access_denied", StringComparison.Ordinal) || code.EndsWith("_busy", StringComparison.Ordinal))
                        ? "文件正被占用或无法访问，稍等几秒再试"
                        : "操作没有完成";
            }
        }

        internal static Dictionary<string, object> Map(params object[] pairs)
        {
            var map = new Dictionary<string, object>();
            for (int index = 0; index + 1 < pairs.Length; index += 2)
            {
                map[(string)pairs[index]] = pairs[index + 1];
            }

            return map;
        }
    }
}
