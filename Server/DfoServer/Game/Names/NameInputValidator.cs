using System;
using System.Text;

namespace DfoServer.Game.Names
{
    public enum NameInputValidationFailure
    {
        None,
        Null,
        TooShort,
        TooLong,
        InvalidUtf8,
        DisallowedUnicodeRange,
        Slang,
        DisallowedCharacter,
    }

    public static class NameInputValidator
    {
        public const byte InvalidNameErrorCode = 0x9F;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool TryValidateRawName(
            byte[] nameBytes,
            int minBytes,
            int maxBytes,
            out string text,
            out NameInputValidationFailure failure)
        {
            text = string.Empty;

            if (nameBytes == null)
            {
                failure = NameInputValidationFailure.Null;
                return false;
            }

            if (nameBytes.Length < minBytes)
            {
                failure = NameInputValidationFailure.TooShort;
                return false;
            }

            if (nameBytes.Length > maxBytes)
            {
                failure = NameInputValidationFailure.TooLong;
                return false;
            }

            if (nameBytes.Length == 0)
            {
                failure = NameInputValidationFailure.None;
                return true;
            }

            try
            {
                text = StrictUtf8.GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                failure = NameInputValidationFailure.InvalidUtf8;
                return false;
            }

            var rules = NameInputRuleSet.Current;
            if (!rules.IsAllowedByUnicodeRange(text))
            {
                failure = NameInputValidationFailure.DisallowedUnicodeRange;
                return false;
            }

            if (rules.HasSlang(text))
            {
                failure = NameInputValidationFailure.Slang;
                return false;
            }

            if (rules.HasSpecialCharacter(nameBytes, text))
            {
                failure = NameInputValidationFailure.DisallowedCharacter;
                return false;
            }

            failure = NameInputValidationFailure.None;
            return true;
        }
    }
}
