using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    internal delegate Task GameCommandHandler(
        EnhancedClientSession session,
        GamePacketHeader header,
        byte[] body);

    // 命令路由唯一写入口。重复命令号必须在启动装配阶段失败，
    // 不能再依赖 Dictionary 索引器静默覆盖和注册顺序。
    internal sealed class GameCommandRegistry
    {
        private readonly Dictionary<ushort, GameCommandHandler> _handlers =
            new Dictionary<ushort, GameCommandHandler>();
        private readonly Dictionary<ushort, string> _sources =
            new Dictionary<ushort, string>();

        internal int Count => _handlers.Count;

        internal void RegisterGroup(
            string source,
            Action<GameCommandRegistrationGroup> register)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Command source is empty.", nameof(source));
            if (register == null)
                throw new ArgumentNullException(nameof(register));

            register(new GameCommandRegistrationGroup(this, source));
        }

        internal bool TryGetValue(
            ushort command,
            out GameCommandHandler handler)
            => _handlers.TryGetValue(command, out handler);

        private void Register(
            ushort command,
            GameCommandHandler handler,
            string source)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (_sources.TryGetValue(command, out var existingSource))
            {
                throw new InvalidOperationException(
                    $"Duplicate game command 0x{command:X4}: "
                    + $"'{source}' conflicts with '{existingSource}'.");
            }

            _handlers.Add(command, handler);
            _sources.Add(command, source);
        }

        internal sealed class GameCommandRegistrationGroup
        {
            private readonly GameCommandRegistry _registry;
            private readonly string _source;

            internal GameCommandRegistrationGroup(
                GameCommandRegistry registry,
                string source)
            {
                _registry = registry
                    ?? throw new ArgumentNullException(nameof(registry));
                _source = source;
            }

            internal GameCommandHandler this[ushort command]
            {
                set => _registry.Register(command, value, _source);
            }
        }
    }
}
