using System;

namespace DfoServer.Game.Accounts
{
    public interface IAccountRepository
    {
        AccountRecord GetById(int accountId);
        AccountRecord GetByMid(string mId);
        int Create(string mId, string passwordHash);
        void UpdateLastLogin(int accountId, string ip, DateTime when);
    }
}
