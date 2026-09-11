using MongoDB.Bson;
using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AscNet.Common.Database;

public sealed partial class Guild
{
    public static readonly IMongoCollection<Guild> collection = Common.db.GetCollection<Guild>("guilds");
    private static readonly IMongoCollection<BsonDocument> counters = Common.db.GetCollection<BsonDocument>("guild_counters");
    private static readonly Lazy<bool> indexes = new(() =>
    {
        // Legacy inactive documents with members are paid-creation reservations, not disposable guilds.
        collection.UpdateMany(new BsonDocument
        {
            { "active", false }, { "creation_reserved", new BsonDocument("$exists", false) },
            { "member_ids.0", new BsonDocument("$exists", true) }
        }, Builders<Guild>.Update.Set(guild => guild.CreationReserved, true).Inc(guild => guild.Version, 1));
        var reserved = Builders<Guild>.Filter.Eq(guild => guild.Active, true) |
            Builders<Guild>.Filter.Eq(guild => guild.CreationReserved, true);
        collection.Indexes.CreateMany(
        [
            new CreateIndexModel<Guild>(Builders<Guild>.IndexKeys.Ascending(guild => guild.Name),
                new CreateIndexOptions<Guild> { Name = "guild_reserved_name", Unique = true,
                    PartialFilterExpression = reserved & Builders<Guild>.Filter.Gt(guild => guild.Name, "") }),
            new CreateIndexModel<Guild>(Builders<Guild>.IndexKeys.Ascending(guild => guild.MemberIds),
                new CreateIndexOptions<Guild> { Name = "guild_reserved_members", Unique = true,
                    PartialFilterExpression = reserved & Builders<Guild>.Filter.Exists("member_ids.0") }),
            new CreateIndexModel<Guild>(Builders<Guild>.IndexKeys.Ascending("pending_operation.Players.Uid"),
                new CreateIndexOptions { Name = "guild_pending_players" }),
            new CreateIndexModel<Guild>(Builders<Guild>.IndexKeys.Ascending(guild => guild.PendingMembershipChanges),
                new CreateIndexOptions { Name = "guild_pending_membership_changes" })
        ]);
        foreach (BsonDocument index in collection.Indexes.List().ToList())
            if (index["name"].AsString is "guild_name" or "guild_members")
                collection.Indexes.DropOne(index["name"].AsString);
        return true;
    });

    [BsonId]
    [BsonRepresentation(BsonType.Int64)]
    public uint Id { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("icon_id")]
    public int IconId { get; set; }

    [BsonElement("level")]
    public int Level { get; set; }

    [BsonElement("leader_id")]
    public long LeaderId { get; set; }

    [BsonElement("declaration")]
    public string Declaration { get; set; } = string.Empty;

    [BsonElement("option")]
    public int Option { get; set; }

    [BsonElement("min_level")]
    public int MinLevel { get; set; }

    [BsonElement("created_at")]
    public long CreatedAt { get; set; }

    [BsonElement("creation_quota_period")]
    [BsonIgnoreIfNull]
    public long? CreationQuotaPeriod { get; set; }

    [BsonElement("creation_costs")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> CreationCosts { get; set; } = [];

    [BsonElement("active")]
    public bool Active { get; set; }

    [BsonElement("creation_reserved")]
    public bool CreationReserved { get; set; }

    [BsonElement("version")]
    public long Version { get; set; }

    [BsonElement("pending_operation")]
    public GuildPendingOperation? PendingOperation { get; set; }

    [BsonElement("pending_membership_changes")]
    public List<long> PendingMembershipChanges { get; set; } = [];

    [BsonElement("members")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<long, GuildMemberState> Members { get; set; } = [];

    [BsonElement("member_ids")]
    public List<long> MemberIds { get; set; } = [];

    [BsonElement("applications")]
    public List<GuildApplication> Applications { get; set; } = [];

    [BsonElement("max_members")]
    public int MaxMembers { get; set; }

    [BsonElement("max_tourists")]
    public int MaxTourists { get; set; }

    public void Normalize()
    {
        foreach (long uid in MemberIds)
        {
            if (!Members.TryGetValue(uid, out GuildMemberState? member))
                Members.Add(uid, member = new GuildMemberState { JoinedAt = CreatedAt, Rank = 4 });
            if (uid == LeaderId) member.Rank = 1;
            else if (member.Rank is < 2 or > 5) member.Rank = 4;
        }
    }

    private static Guild? Normalized(Guild? guild)
    {
        guild?.Normalize();
        return guild;
    }

    public void SaveChecked()
    {
        _ = indexes.Value;
        Normalize();
        long previous = Version;
        var filter = Builders<Guild>.Filter;
        var version = filter.Eq(guild => guild.Version, previous);
        if (previous == 0) version |= filter.Exists("version", false);
        Version = checked(previous + 1);
        try
        {
            ReplaceOneResult result = collection.ReplaceOne(filter.Eq(guild => guild.Id, Id) & version, this);
            if (!result.IsAcknowledged || result.MatchedCount != 1)
                throw new InvalidOperationException("Guild changed concurrently or no longer exists.");
        }
        catch
        {
            Version = previous;
            throw;
        }
    }

    public static Guild? FindByMember(long playerId) =>
        Normalized(collection.Find(guild => guild.Active && guild.MemberIds.Contains(playerId)).FirstOrDefault());

    public static Guild? FindById(uint id) =>
        Normalized(collection.Find(guild => guild.Id == id).FirstOrDefault());

    public static Guild? FindPendingByFounder(long id)
    {
        _ = indexes.Value;
        return Normalized(collection.Find(guild => !guild.Active && guild.CreationReserved && guild.LeaderId == id).FirstOrDefault());
    }

    public static List<Guild> FindPendingByParticipant(long uid) =>
        collection.Find(guild => guild.PendingOperation != null &&
            (guild.PendingOperation.ActorId == uid || guild.PendingOperation.Players.Any(player => player.Uid == uid))).ToList();

    public static List<Guild> FindPendingMembershipChanges(long uid) =>
        collection.Find(guild => guild.PendingMembershipChanges.Contains(uid)).ToList();

    public static List<Guild> AllActive()
    {
        List<Guild> guilds = collection.Find(guild => guild.Active).ToList();
        foreach (Guild guild in guilds) guild.Normalize();
        return guilds;
    }

    public static Guild ReserveCreation(Guild requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentException.ThrowIfNullOrWhiteSpace(requested.Name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requested.LeaderId);
        _ = indexes.Value;
        Guild? existing = collection.Find(guild => (guild.Active || guild.CreationReserved) && guild.MemberIds.Contains(requested.LeaderId)).FirstOrDefault();
        if (existing is not null)
            return existing;

        requested.Id = AllocateId();
        requested.Active = false;
        requested.CreationReserved = true;
        requested.Version = 1;
        requested.MemberIds = [requested.LeaderId];
        requested.Applications = [];
        requested.CreationQuotaPeriod = null;
        requested.Normalize();
        try
        {
            collection.InsertOne(requested);
            return requested;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another request may have reserved or activated this founder while we allocated an ID.
            existing = collection.Find(guild => (guild.Active || guild.CreationReserved) && guild.MemberIds.Contains(requested.LeaderId)).FirstOrDefault();
            if (existing is not null)
                return existing;
            throw;
        }
    }

    public static void EnsureCreationQuota(Guild guild, long resetPeriod, int dailyLimit)
    {
        ArgumentNullException.ThrowIfNull(guild);
        ArgumentOutOfRangeException.ThrowIfNegative(resetPeriod);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dailyLimit);
        Guild persisted = FindById(guild.Id)
            ?? throw new InvalidOperationException("Guild creation reservation is missing.");
        if (persisted.LeaderId != guild.LeaderId || (!persisted.Active && !persisted.CreationReserved))
            throw new InvalidOperationException("Guild creation founder does not match.");
        if (persisted.CreationQuotaPeriod is not null)
        {
            guild.CreationQuotaPeriod = persisted.CreationQuotaPeriod;
            return;
        }

        var filter = Builders<BsonDocument>.Filter;
        var period = filter.Eq("_id", $"creation:{resetPeriod}");
        try
        {
            counters.UpdateOne(period, Builders<BsonDocument>.Update.SetOnInsert("founder_ids", new BsonArray()),
                new UpdateOptions { IsUpsert = true });
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another creator initialized this reset period concurrently.
        }
        var room = new BsonDocument("$expr", new BsonDocument("$lt", new BsonArray
        {
            new BsonDocument("$size", "$founder_ids"), dailyLimit
        }));
        UpdateResult quota = counters.UpdateOne(
            period & (filter.AnyEq("founder_ids", guild.LeaderId) | room),
            Builders<BsonDocument>.Update.AddToSet("founder_ids", guild.LeaderId));
        if (quota.MatchedCount == 0)
            throw new InvalidOperationException("GuildCreateReachDailyLimit");

        // A crash here can conservatively consume a second day's slot on retry; neither
        // day's cap can be exceeded. Never remove a slot another creator may be using.
        Guild? recorded = collection.FindOneAndUpdate(
            Builders<Guild>.Filter.Where(value => value.Id == guild.Id && value.LeaderId == guild.LeaderId &&
                (value.Active || value.CreationReserved) && value.CreationQuotaPeriod == null && value.PendingOperation == null),
            Builders<Guild>.Update.Set(value => value.CreationQuotaPeriod, (long?)resetPeriod).Inc(value => value.Version, 1),
            new FindOneAndUpdateOptions<Guild> { ReturnDocument = ReturnDocument.After });
        recorded ??= FindById(guild.Id);
        guild.CreationQuotaPeriod = recorded?.CreationQuotaPeriod
            ?? throw new InvalidOperationException("Guild creation reservation is missing.");
    }

    private static uint AllocateId()
    {
        // EN XUiGuildRecommendation.lua:182-185 accepts exactly eight digits.
        const long minimum = 10_000_000;
        const long maximum = 99_999_999;
        var filter = Builders<BsonDocument>.Filter.Eq("_id", "guild");
        try
        {
            counters.UpdateOne(filter, Builders<BsonDocument>.Update.Max("sequence", minimum - 1),
                new UpdateOptions { IsUpsert = true });
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another allocator initialized the counter concurrently.
        }
        BsonDocument? counter = counters.FindOneAndUpdate(
            filter & Builders<BsonDocument>.Filter.Lt("sequence", maximum),
            Builders<BsonDocument>.Update.Inc("sequence", 1L),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });
        if (counter is null)
            throw new InvalidOperationException("The eight-digit guild ID range is exhausted.");
        return checked((uint)counter["sequence"].AsInt64);
    }

    public static Guild? Activate(uint id, long founderId)
    {
        _ = indexes.Value;
        return collection.FindOneAndUpdate(
            Builders<Guild>.Filter.Where(guild => guild.Id == id && guild.LeaderId == founderId && guild.MemberIds.Contains(founderId)
                && (guild.Active || guild.CreationReserved) && guild.PendingOperation == null && guild.CreationQuotaPeriod != null),
            Builders<Guild>.Update.Set(guild => guild.Active, true).Set(guild => guild.CreationReserved, false).Inc(guild => guild.Version, 1),
            new FindOneAndUpdateOptions<Guild> { ReturnDocument = ReturnDocument.After });
    }

    public static Guild? TryAdmit(uint id, long playerId, int capacity, long? applicationCutoff)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(playerId);
        if (capacity <= 0)
            return null;
        _ = indexes.Value;
        Guild? guild = FindById(id);
        if (guild is not { Active: true, PendingOperation: null }) return null;
        // Existing membership is a successful retry even if the guild has since filled up.
        if (!guild.MemberIds.Contains(playerId))
        {
            // EN XGuildConfig.ApplySetting.NoneApply = 1; approval needs a still-live application.
            bool admitted = applicationCutoff is long cutoff
                ? guild.Applications.Any(application => application.PlayerId == playerId && application.CreatedAt > cutoff)
                : guild.Option == 1;
            if (!admitted || guild.MemberIds.Count >= capacity) return null;
            guild.MemberIds.Add(playerId);
            guild.Members[playerId] = new GuildMemberState
            {
                Rank = 4, JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }
        guild.Applications.RemoveAll(application => application.PlayerId == playerId);
        try
        {
            // The version predicate guards the membership, approval and capacity snapshot together.
            guild.SaveChecked();
            return guild;
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            return null;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return null;
        }
    }

    public static Guild? AddApplication(uint id, long playerId, long now, int maxCount, long timeoutSecs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(playerId);
        ArgumentOutOfRangeException.ThrowIfNegative(now);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSecs);
        _ = indexes.Value;
        long cutoff = checked(now - timeoutSecs);
        var liveApplications = new BsonDocument("$filter", new BsonDocument
        {
            { "input", "$applications" },
            { "as", "application" },
            { "cond", new BsonDocument("$gt", new BsonArray { "$$application.created_at", cutoff }) }
        });
        var filter = Builders<Guild>.Filter;
        var room = new BsonDocument("$expr", new BsonDocument("$lt", new BsonArray
        {
            new BsonDocument("$size", liveApplications), maxCount
        }));
        var existing = filter.ElemMatch(guild => guild.Applications,
            application => application.PlayerId == playerId && application.CreatedAt > cutoff);
        PipelineDefinition<Guild, Guild> pipeline = new BsonDocument[]
        {
            new("$set", new BsonDocument("applications", liveApplications)),
            new("$set", new BsonDocument("version", new BsonDocument("$add", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$version", 0L }), 1L
            }))),
            new("$set", new BsonDocument("applications", new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$in", new BsonArray { playerId, "$applications.player_id" }),
                "$applications",
                new BsonDocument("$concatArrays", new BsonArray
                {
                    "$applications",
                    new BsonArray { new BsonDocument { { "player_id", playerId }, { "created_at", now } } }
                })
            })))
        };
        return collection.FindOneAndUpdate(
            filter.Where(guild => guild.Id == id && guild.Active && guild.PendingOperation == null && !guild.MemberIds.Contains(playerId)) &
                (existing | room),
            Builders<Guild>.Update.Pipeline(pipeline),
            new FindOneAndUpdateOptions<Guild> { ReturnDocument = ReturnDocument.After });
    }

    public static Guild? RemoveApplication(uint id, long playerId)
    {
        _ = indexes.Value;
        return collection.FindOneAndUpdate(Builders<Guild>.Filter.Where(guild => guild.Id == id && guild.Active && guild.PendingOperation == null),
            Builders<Guild>.Update.PullFilter(guild => guild.Applications, application => application.PlayerId == playerId).Inc(guild => guild.Version, 1),
            new FindOneAndUpdateOptions<Guild> { ReturnDocument = ReturnDocument.After });
    }
}

public sealed class GuildApplication
{
    [BsonElement("player_id")]
    public long PlayerId { get; set; }

    [BsonElement("created_at")]
    public long CreatedAt { get; set; }
}

public sealed partial class GuildMemberState
{
    public int Rank { get; set; } = 4;
    public long JoinedAt { get; set; }
    public int WeekContribute { get; set; }
    public int TotalContribute { get; set; }
    public int ActiveContribute { get; set; }
    public int Popularity { get; set; }
}

public sealed class GuildPendingOperation
{
    public string Id { get; set; } = string.Empty;
    public long ActorId { get; set; }
    public int RequestId { get; set; }
    public string RequestKey { get; set; } = string.Empty;
    public string ResponseName { get; set; } = string.Empty;
    public byte[] ResponseBody { get; set; } = [];
    public byte[] GuildState { get; set; } = [];
    public List<GuildPendingPlayer> Players { get; set; } = [];
    public List<GuildPendingPush> Pushes { get; set; } = [];
}

public sealed class GuildPendingPlayer
{
    public long Uid { get; set; }
    public GuildPlayerState State { get; set; } = new();
    public List<GuildPendingGrant> Grants { get; set; } = [];
    public List<int> ClaimedTasks { get; set; } = [];
    public List<PlayerMail> Mails { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ConditionCounters { get; set; } = [];
}

public sealed class GuildPendingGrant
{
    public string ClaimKey { get; set; } = string.Empty;
    public List<RewardGoods> Goods { get; set; } = [];
    public List<List<int>> GoodsParams { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Costs { get; set; } = [];
}

public sealed class GuildPendingPush
{
    public long Uid { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[] Body { get; set; } = [];
}

public sealed class GuildRequestReceipt
{
    public int RequestId { get; set; }
    public string RequestKey { get; set; } = string.Empty;
    public string ResponseName { get; set; } = string.Empty;
    public byte[] ResponseBody { get; set; } = [];
}
