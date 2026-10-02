using System;
using System.Globalization;

namespace NoReturnGuardian.Core
{
    public static class WarmReloadClassifications
    {
        public const string TargetGenerationExact = "target_generation_exact";
        public const string TargetLineageCloser = "target_lineage_closer";
        public const string CachedOriginalReasserted = "cached_original_reasserted";
        public const string TargetCounterNotRestored = "target_counter_not_restored";
        public const string UnexpectedRunIdentity = "unexpected_run_identity";
        public const string NonHideoutState = "non_hideout_state";
        public const string AmbiguousGeneration = "ambiguous_generation";
    }

    public sealed class WarmReloadLineageEvidence
    {
        public string Classification { get; set; }
        public bool TargetLineageSupported { get; set; }
        public bool ExactTargetObserved { get; set; }
        public long TargetByteDistance { get; set; }
        public long OriginalByteDistance { get; set; }
        public string TargetStateCode { get; set; }
        public string OriginalStateCode { get; set; }
        public string ObservedStateCode { get; set; }
    }

    public static class WarmReloadLineageAnalyzer
    {
        public static Result<WarmReloadLineageEvidence> Analyze(
            string targetStateCode,
            string originalStateCode,
            string observedStateCode,
            byte[] targetRun,
            byte[] originalRun,
            byte[] observedRun)
        {
            ParsedStateCode target;
            ParsedStateCode original;
            ParsedStateCode observed;
            if (!TryParse(targetStateCode, out target)
                || !TryParse(originalStateCode, out original)
                || !TryParse(observedStateCode, out observed))
            {
                return Result<WarmReloadLineageEvidence>.Fail(
                    "warm_reload_state_code_invalid",
                    "Target, original, and observed state codes must be 24 hexadecimal characters.");
            }

            if (targetRun == null || originalRun == null || observedRun == null)
            {
                return Result<WarmReloadLineageEvidence>.Fail(
                    "warm_reload_payload_missing",
                    "Target, original, and observed raw R0A payloads are required.");
            }

            long targetDistance = ByteDistance(targetRun, observedRun);
            long originalDistance = ByteDistance(originalRun, observedRun);
            WarmReloadLineageEvidence evidence = new WarmReloadLineageEvidence
            {
                TargetByteDistance = targetDistance,
                OriginalByteDistance = originalDistance,
                TargetStateCode = targetStateCode.ToUpperInvariant(),
                OriginalStateCode = originalStateCode.ToUpperInvariant(),
                ObservedStateCode = observedStateCode.ToUpperInvariant()
            };

            if (!string.Equals(
                observed.StateWord,
                "00800000",
                StringComparison.Ordinal))
            {
                return Classified(
                    evidence,
                    WarmReloadClassifications.NonHideoutState,
                    false,
                    false);
            }

            if (!string.Equals(
                target.IdentityWord,
                observed.IdentityWord,
                StringComparison.Ordinal))
            {
                return Classified(
                    evidence,
                    WarmReloadClassifications.UnexpectedRunIdentity,
                    false,
                    false);
            }

            if (targetDistance == 0
                && string.Equals(
                    targetStateCode,
                    observedStateCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Classified(
                    evidence,
                    WarmReloadClassifications.TargetGenerationExact,
                    true,
                    true);
            }

            if (originalDistance == 0
                || string.Equals(
                    originalStateCode,
                    observedStateCode,
                    StringComparison.OrdinalIgnoreCase)
                    && originalDistance <= targetDistance)
            {
                return Classified(
                    evidence,
                    WarmReloadClassifications.CachedOriginalReasserted,
                    false,
                    false);
            }

            bool restoringOlderGeneration = string.Equals(
                    target.IdentityWord,
                    original.IdentityWord,
                    StringComparison.Ordinal)
                && target.Counter < original.Counter;
            if (restoringOlderGeneration
                && observed.Counter >= original.Counter)
            {
                return Classified(
                    evidence,
                    WarmReloadClassifications.TargetCounterNotRestored,
                    false,
                    false);
            }

            if (observed.Counter >= target.Counter
                && targetDistance < originalDistance)
            {
                return Classified(
                    evidence,
                    WarmReloadClassifications.TargetLineageCloser,
                    true,
                    false);
            }

            return Classified(
                evidence,
                WarmReloadClassifications.AmbiguousGeneration,
                false,
                false);
        }

        private static Result<WarmReloadLineageEvidence> Classified(
            WarmReloadLineageEvidence evidence,
            string classification,
            bool supported,
            bool exact)
        {
            evidence.Classification = classification;
            evidence.TargetLineageSupported = supported;
            evidence.ExactTargetObserved = exact;
            return Result<WarmReloadLineageEvidence>.Ok(
                evidence,
                classification,
                classification);
        }

        private static long ByteDistance(byte[] first, byte[] second)
        {
            int shared = Math.Min(first.Length, second.Length);
            long different = Math.Abs((long)first.Length - second.Length);
            for (int index = 0; index < shared; index++)
            {
                if (first[index] != second[index])
                {
                    different++;
                }
            }

            return different;
        }

        private static bool TryParse(string value, out ParsedStateCode parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(value) || value.Length != 24)
            {
                return false;
            }

            uint counter;
            if (!uint.TryParse(
                value.Substring(16, 8),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out counter))
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

            parsed = new ParsedStateCode
            {
                IdentityWord = value.Substring(0, 8).ToUpperInvariant(),
                StateWord = value.Substring(8, 8).ToUpperInvariant(),
                Counter = counter
            };
            return true;
        }

        private sealed class ParsedStateCode
        {
            public string IdentityWord { get; set; }
            public string StateWord { get; set; }
            public uint Counter { get; set; }
        }
    }
}
