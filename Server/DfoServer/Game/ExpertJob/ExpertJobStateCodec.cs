using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Game.ExpertJob
{
    internal static class ExpertJobStateCodec
    {
        internal const byte EnchanterType = 1;
        internal const byte AlchemistType = 2;
        internal const byte DisjointerType = 3;
        internal const byte DollControllerType = 4;
        internal const byte DisjointerMode = 3;

        internal static void ProjectToSnapshot(
            int expertJobType,
            ExpertJobState state,
            ExpertJobInfoSnapshot snapshot,
            uint expertJobExperience = 0)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            snapshot.State0 = (byte)Math.Min(byte.MaxValue, Math.Max(0, state?.GiveUpCount ?? 0));
            snapshot.Mode = expertJobType > 0 && expertJobType <= byte.MaxValue
                ? (byte)expertJobType
                : (byte)0;
            snapshot.DisjointMachineGrade = 0;
            snapshot.DisjointMachineEndurance = 0;
            snapshot.Entries.Clear();
            snapshot.CardQualificationLevels.Clear();
            snapshot.EnchanterLevel = 0;
            snapshot.EnchanterEndurance = 0;

            if (expertJobType == DisjointerType)
            {
                var machine = state?.DisjointMachine;
                if (machine == null)
                {
                    snapshot.DisjointMachineGrade = 1;
                    snapshot.DisjointMachineEndurance =
                        DisjointMachineConfigProvider.InitialEndurance;
                    return;
                }

                snapshot.DisjointMachineGrade = machine.MachineGrade;
                snapshot.DisjointMachineEndurance = machine.Endurance;
                return;
            }

            if (state == null)
                return;

            foreach (var recipeId in state.LearnedRecipeIds)
                snapshot.Entries.Add(recipeId);

            if (expertJobType == EnchanterType)
            {
                var config = EnchanterConfigProvider.Config;
                snapshot.CardQualificationLevels.AddRange(
                    config.GetCardQualificationLevels(expertJobExperience));
                snapshot.EnchanterLevel = config.GetLevel(expertJobExperience);
                snapshot.EnchanterEndurance = state.EnchanterMachine?.Endurance
                    ?? config.InitialEndurance;
            }
        }

        internal static bool TryDecodeLegacyBlob(
            byte[] blob,
            out byte mode,
            out ExpertJobState state)
        {
            mode = 0;
            state = null;
            if (blob == null || blob.Length < 2)
                return false;

            mode = blob[1];
            var decoded = new ExpertJobState
            {
                GiveUpCount = blob[0],
            };

            var offset = 2;
            if (offset + 8 > blob.Length)
                return false;

            var grade = BitConverter.ToInt32(blob, offset);
            offset += 4;
            var endurance = BitConverter.ToInt32(blob, offset);
            offset += 4;
            if (mode == DisjointerMode)
            {
                if (grade < 0 || grade > byte.MaxValue || endurance < 0)
                    return false;
                decoded.DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = (byte)Math.Max(1, grade),
                    Endurance = endurance,
                };
            }

            if (offset < blob.Length)
            {
                var count = blob[offset++];
                if (blob.Length - offset < count * 4)
                    return false;
                for (var index = 0; index < count; index++)
                {
                    decoded.LearnedRecipeIds.Add(BitConverter.ToInt32(blob, offset));
                    offset += 4;
                }
            }

            state = decoded;
            return true;
        }
    }
}
