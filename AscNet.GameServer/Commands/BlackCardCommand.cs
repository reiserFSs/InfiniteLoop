using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;

namespace AscNet.GameServer.Commands
{
    [CommandName("bc")]
    internal class BlackCardCommand : Command
    {
        private const int DefaultBlackCardGrant = 30_000;

        public BlackCardCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Grant Black Cards to the current online player. Usage: bc [amount|max]; default is 30000.";

        [Argument(0, @"^[1-9][0-9]*$|^max$", "Black Card amount to grant, or max", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        string Amount { get; set; } = string.Empty;

        public override void Execute()
        {
            int amount = string.IsNullOrEmpty(Amount)
                ? DefaultBlackCardGrant
                : Amount.Equals("max", StringComparison.OrdinalIgnoreCase)
                    ? int.MaxValue
                    : Miscs.ParseIntOr(Amount);

            Item blackCards = session.inventory.Do(Inventory.FreeGem, amount);
            session.inventory.Save();
            session.SendPush(new NotifyItemDataList
            {
                ItemDataList = { blackCards }
            });
        }
    }
}
