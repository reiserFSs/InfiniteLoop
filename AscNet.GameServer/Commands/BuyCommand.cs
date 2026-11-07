using System;
using AscNet.GameServer.Handlers;

namespace AscNet.GameServer.Commands
{
    [CommandName("buy")]
    internal class BuyCommand : Command
    {
        public BuyCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Buy a store package by id (GM panel). Usage: buy <packageId> [count]";

        [Argument(0, @"^[0-9]+$", "Package id from client_purchases.json")]
        string Id { get; set; } = string.Empty;

        [Argument(1, @"^[0-9]+$", "How many to buy (default 1)", ArgumentFlags.Optional)]
        string Count { get; set; } = string.Empty;

        public override void Execute()
        {
            uint id = uint.Parse(Id);
            int count = string.IsNullOrEmpty(Count) ? 1 : int.Parse(Count);
            int code = PayModule.PurchaseForGm(session, id, count);
            if (code != 0)
                throw new InvalidOperationException($"Purchase {id} failed, code={code}");
        }
    }
}