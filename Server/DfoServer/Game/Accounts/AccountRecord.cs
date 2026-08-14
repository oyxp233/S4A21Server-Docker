using System;

namespace DfoServer.Game.Accounts
{
    public sealed class AccountRecord
    {
        public int AccountId { get; set; }
        public string MId { get; set; }
        public string PasswordHash { get; set; }
        public string LastLoginIp { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
