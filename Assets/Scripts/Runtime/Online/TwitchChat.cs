using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Online
{
    /// <summary>
    /// Twitch chat plays, read-only. A channel's chat is read anonymously over Twitch's IRC WebSocket (a nick of
    /// "justinfan" and a number is Twitch's read-only guest login: no account, no token, no password). Nothing is
    /// sent but that login, the JOIN and the PONG that keeps the connection open; nothing is ever written to chat.
    ///
    /// The socket runs on a background task and puts each chat line on a queue; <see cref="Poll"/>, on the main
    /// thread, takes them off. A message starting with "!" is a vote: one standing vote a viewer (the last one
    /// counts), read by <see cref="Votes"/> ("!red", "!blue", or a boxer's name). "!cube" asks for a cube to be
    /// thrown into the ring; <see cref="TakeCube"/> hands out at most one every few seconds.
    ///
    /// Off unless PlayerPrefs "pobox.twitch" holds a channel name. YouTube's live chat has no anonymous read: it
    /// needs a Google API key, and a key shipped in an app is a key given away, so it is not here.
    /// </summary>
    public sealed class TwitchChat : IDisposable
    {
        public const string PrefKey = "pobox.twitch";
        const string Server = "wss://irc-ws.chat.twitch.tv:443";
        /// <summary>Seconds between two cubes, however many are asked for.</summary>
        public float cubeEvery = 4f;

        public string Channel { get; }
        public bool Connected => _connected;

        readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        readonly CancellationTokenSource _stop = new CancellationTokenSource();
        readonly Dictionary<string, string> _votes = new Dictionary<string, string>();
        volatile bool _connected;
        int _cubesAsked;
        float _nextCube;

        /// <summary>The channel in the settings, connecting; null when there is none (chat plays is off).</summary>
        public static TwitchChat FromPrefs()
        {
            string channel = PlayerPrefs.GetString(PrefKey, "").Trim().TrimStart('#').ToLowerInvariant();
            return channel.Length == 0 ? null : new TwitchChat(channel);
        }

        public TwitchChat(string channel)
        {
            Channel = channel;
            Task.Run(() => Run(_stop.Token));
        }

        public void Dispose() => _stop.Cancel();

        async Task Run(CancellationToken stop)
        {
            var rng = new System.Random();
            var buffer = new byte[8192];
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var ws = new ClientWebSocket())
                    {
                        await ws.ConnectAsync(new Uri(Server), stop);
                        await Send(ws, "NICK justinfan" + rng.Next(10000, 99999), stop);
                        await Send(ws, "JOIN #" + Channel, stop);
                        _connected = true;
                        var message = new MemoryStream();
                        while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
                        {
                            WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stop);
                            if (r.MessageType == WebSocketMessageType.Close) break;
                            message.Write(buffer, 0, r.Count);
                            if (!r.EndOfMessage) continue;
                            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                            message.SetLength(0);
                            foreach (string line in text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (line.StartsWith("PING", StringComparison.Ordinal)) await Send(ws, "PONG" + line.Substring(4), stop);
                                else _lines.Enqueue(line);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { /* dropped or refused: try again in a moment */ }
                _connected = false;
                try { await Task.Delay(5000, stop); }
                catch (OperationCanceledException) { break; }
            }
            _connected = false;
        }

        static Task Send(ClientWebSocket ws, string line, CancellationToken stop) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(line + "\r\n")), WebSocketMessageType.Text, true, stop);

        /// <summary>On the main thread: the chat that has come in since last time, counted.</summary>
        public void Poll()
        {
            while (_lines.TryDequeue(out string line))
            {
                // :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #channel :!red
                int bang = line.IndexOf('!'), say = line.IndexOf(" PRIVMSG ", StringComparison.Ordinal);
                if (!line.StartsWith(":") || bang < 2 || say < 0) continue;
                int colon = line.IndexOf(" :", say, StringComparison.Ordinal);
                if (colon < 0) continue;
                string viewer = line.Substring(1, bang - 1);
                string text = line.Substring(colon + 2).Trim();
                if (text.Length < 2 || text[0] != '!') continue;
                int end = text.IndexOf(' ');
                string word = (end < 0 ? text.Substring(1) : text.Substring(1, end - 1)).ToLowerInvariant();
                if (word == "cube") _cubesAsked++;
                else if (word.Length <= 24) _votes[viewer] = word;
            }
        }

        /// <summary>How many viewers' standing vote is this word (lower case).</summary>
        public int Votes(string word)
        {
            int n = 0;
            foreach (string v in _votes.Values) if (v == word) n++;
            return n;
        }

        /// <summary>The word most viewers are voting for, and how many; null with no votes.</summary>
        public string Leader(out int votes)
        {
            var count = new Dictionary<string, int>();
            string best = null;
            votes = 0;
            foreach (string v in _votes.Values)
            {
                count.TryGetValue(v, out int n);
                count[v] = ++n;
                if (n > votes) { votes = n; best = v; }
            }
            return best;
        }

        public void ClearVotes()
        {
            _votes.Clear();
            _cubesAsked = 0;
        }

        /// <summary>True when chat has asked for a cube and the last one was long enough ago.</summary>
        public bool TakeCube()
        {
            if (_cubesAsked == 0 || Time.unscaledTime < _nextCube) return false;
            _cubesAsked = 0;            // asked for by many at once, it is still one cube
            _nextCube = Time.unscaledTime + cubeEvery;
            return true;
        }

        /// <summary>
        /// A cube from the arena's pool (Mj/MjCubePool.cs) thrown at a boxer's chest, from a metre and a half away on
        /// the side towards the middle of the ring, as the trainer throws them. False when the pool is not bound
        /// to the running model (the arena's ring has to bind and tick it), or every cube is still in play.
        /// </summary>
        public static bool ThrowCube(Fighter at)
        {
            var pool = UnityEngine.Object.FindAnyObjectByType<Mj.MjCubePool>();
            if (pool == null || pool.Count == 0 || at == null || at.torso == null || at.ring == null) return false;
            Vector3 c = at.torso.transform.position;
            Vector2 middle = at.ring.centre;
            double towards = Math.Atan2(middle.y - c.z, middle.x - c.x);
            double bearing = towards + (UnityEngine.Random.value - 0.5f) * 2.0;
            // MuJoCo's frame: Unity's x, z, y.
            return pool.Throw(new double[] { c.x, c.z, c.y }, 5.0, bearing, 1.5, 0.3);
        }
    }
}
