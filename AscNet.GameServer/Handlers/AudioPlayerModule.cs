using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.miniactivity.musicplayer;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.photomode;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    /// <summary>
    /// AudioPlayer (music CD) favorites and background playlist. State is durable per-player
    /// (ordered lists), mutated then saved before a Code=0 response; no pushes. Table-driven
    /// caps, default song, and valid song ids come from the authoritative MusicPlayer tables.
    /// </summary>
    internal static class AudioPlayerModule
    {
        private const int SuccessCode = 0;
        private const int ErrorCode = 1; // retail failure code unobserved; any non-zero signals failure

        // MusicPlayerConfig rows are singletons keyed by name.
        private static readonly Lazy<IReadOnlyDictionary<string, int>> ConfigByName = new(() =>
            TableReaderV2.Parse<MusicPlayerConfigTable>()
                .ToDictionary(row => row.Key, row => row.Values));

        private static readonly Lazy<IReadOnlyDictionary<int, MusicPlayerAlbumTable>> Albums = new(() =>
            TableReaderV2.Parse<MusicPlayerAlbumTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<IReadOnlyDictionary<int, ConditionTable>> Conditions = new(() =>
            TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<HashSet<int>> FreeBackgroundIds = new(() =>
            TableReaderV2.Parse<BackgroundTable>()
                .Where(background => background.IsFree > 0)
                .Select(background => background.Id)
                .ToHashSet());

        private static int Config(string key)
            => ConfigByName.Value.TryGetValue(key, out int value) ? value : 0;

        private static int FavoriteMaxCount => Config("FavoriteSongMaxCount");
        private static int BackgroundMaxCount => Config("BackgroundSongMaxCount");

        /// <summary>Default background song; also serves as the implicit owned baseline.</summary>
        internal static int DefaultBackgroundSongId
        {
            get
            {
                int id = Config("DefaultBackgroundSongId");
                // Fall back to a stable default if the table is missing so login never breaks.
                return Albums.Value.ContainsKey(id) ? id : 1;
            }
        }

        internal static bool IsValidSongId(Player player, int songId)
        {
            if (songId == DefaultBackgroundSongId)
                return true;
            if (!Albums.Value.TryGetValue(songId, out MusicPlayerAlbumTable? album))
                return false;
            if (album.ConditionId is null or 0)
                return true;
            if (!Conditions.Value.TryGetValue(album.ConditionId.Value, out ConditionTable? condition)
                || condition.Type != 11117
                || condition.Params.Count < 1)
            {
                return false;
            }

            int backgroundId = condition.Params[0];
            return player.UseBackgroundId == backgroundId
                || player.OwnedBackgroundIds?.Contains(backgroundId) == true
                || FreeBackgroundIds.Value.Contains(backgroundId);
        }

        /// <summary>Builds the durable login payload from player state, seeding the default background song.</summary>
        internal static AudioPlayerLoginData BuildLoginData(Player player)
        {
            List<int> background = player.BackgroundSongs ??= new();
            if (background.Count == 0)
                background.Add(DefaultBackgroundSongId);
            return new AudioPlayerLoginData
            {
                FavoriteSongs = player.FavoriteSongs ?? new(),
                BackgroundSongs = background
            };
        }
        private static bool TrySave(Player player, List<int> songs, List<int> previous)
        {
            try
            {
                player.SaveChecked();
                return true;
            }
            catch
            {
                songs.Clear();
                songs.AddRange(previous);
                return false;
            }
        }

        [RequestPacketHandler("AddAudioPlayerFavoriteSongRequest")]
        public static void AddFavoriteSong(Session session, Packet.Request packet)
        {
            var request = packet.Deserialize<AddAudioPlayerFavoriteSongRequest>();
            var response = new AddAudioPlayerFavoriteSongResponse();
            if (!IsValidSongId(session.player, request.SongId))
            {
                response.Code = ErrorCode;
                session.SendResponse(response, packet.Id);
                return;
            }
            List<int> songs = session.player.FavoriteSongs ??= new();
            if (songs.Contains(request.SongId))
            {
                // Retail duplicate semantics unobserved; conservatively idempotent no-op success.
                response.Code = SuccessCode;
                session.SendResponse(response, packet.Id);
                return;
            }
            List<int> previous = songs.ToList();
            songs.Insert(0, request.SongId);
            if (FavoriteMaxCount > 0 && songs.Count > FavoriteMaxCount)
                songs.RemoveRange(FavoriteMaxCount, songs.Count - FavoriteMaxCount);
            response.Code = TrySave(session.player, songs, previous) ? SuccessCode : ErrorCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("RemoveAudioPlayerFavoriteSongRequest")]
        public static void RemoveFavoriteSong(Session session, Packet.Request packet)
        {
            var request = packet.Deserialize<RemoveAudioPlayerFavoriteSongRequest>();
            var response = new RemoveAudioPlayerFavoriteSongResponse();
            List<int> songs = session.player.FavoriteSongs ??= new();
            List<int> previous = songs.ToList();
            if (songs.Remove(request.SongId))
                response.Code = TrySave(session.player, songs, previous) ? SuccessCode : ErrorCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("AddAudioPlayerBackgroundSongRequest")]
        public static void AddBackgroundSongs(Session session, Packet.Request packet)
        {
            var request = packet.Deserialize<AddAudioPlayerBackgroundSongRequest>();
            var response = new AddAudioPlayerBackgroundSongResponse();
            if (request.SongIds is null || request.SongIds.Any(songId => !IsValidSongId(session.player, songId)))
            {
                response.Code = ErrorCode;
                session.SendResponse(response, packet.Id);
                return;
            }
            List<int> songs = session.player.BackgroundSongs ??= new();
            List<int> previous = songs.ToList();
            bool mutated = false;
            int max = BackgroundMaxCount;
            foreach (int songId in request.SongIds)
            {
                if (songs.Contains(songId))
                    continue;
                songs.Insert(0, songId);
                mutated = true;
                if (max > 0 && songs.Count > max)
                    songs.RemoveRange(max, songs.Count - max);
            }
            if (mutated)
                response.Code = TrySave(session.player, songs, previous) ? SuccessCode : ErrorCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("RemoveAudioPlayerBackgroundSongRequest")]
        public static void RemoveBackgroundSong(Session session, Packet.Request packet)
        {
            var request = packet.Deserialize<RemoveAudioPlayerBackgroundSongRequest>();
            var response = new RemoveAudioPlayerBackgroundSongResponse();
            List<int> songs = session.player.BackgroundSongs ??= new();
            if (songs.Count == 1 && songs.Contains(request.SongId))
            {
                response.Code = ErrorCode;
                session.SendResponse(response, packet.Id);
                return;
            }

            List<int> previous = songs.ToList();
            if (songs.Remove(request.SongId))
                response.Code = TrySave(session.player, songs, previous) ? SuccessCode : ErrorCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("ResetAudioPlayerBackgroundSongRequest")]
        public static void ResetBackgroundSongs(Session session, Packet.Request packet)
        {
            packet.Deserialize<ResetAudioPlayerBackgroundSongRequest>();
            var response = new ResetAudioPlayerBackgroundSongResponse();
            List<int> songs = session.player.BackgroundSongs ??= new();
            List<int> previous = songs.ToList();
            songs.Clear();
            songs.Add(DefaultBackgroundSongId);
            response.Code = songs.SequenceEqual(previous) || TrySave(session.player, songs, previous)
                ? SuccessCode
                : ErrorCode;
            response.BackgroundSongs = new List<int>(songs);
            session.SendResponse(response, packet.Id);
        }
    }
}
