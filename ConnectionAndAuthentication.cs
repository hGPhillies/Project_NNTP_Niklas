using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Project_NNTP_Niklas
{
    public sealed class ConnectionAndAuthentication
    {
        public record AuthenticationResult(bool Success, string Message, string ServerGreeting, string LastResponse);

        /// <summary>
        /// Connects to an NNTP server and performs AUTHINFO USER / PASS if username supplied.
        /// Returns an AuthenticationResult describing success and server replies.
        /// Non-blocking and safe to call from UI code (await from an async event handler).
        /// </summary>
        public async Task<AuthenticationResult> AuthenticateAsync(string host, int port, string username, string password, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));
            if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));

            TcpClient? client = null;

            try
            {
                client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != connectTask)
                {
                    return new AuthenticationResult(false, "Connection timed out.", string.Empty, string.Empty);
                }

                if (!client.Connected)
                {
                    return new AuthenticationResult(false, "Failed to connect.", string.Empty, string.Empty);
                }

                using NetworkStream ns = client.GetStream();
                // Set a read timeout for synchronous reads as a fallback (we use async reads below)
                ns.ReadTimeout = timeoutMs;

                using var reader = new StreamReader(ns, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(ns, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true
                };

                // Read server greeting
                string? greeting = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                var lastResponse = greeting ?? string.Empty;

                if (string.IsNullOrWhiteSpace(username))
                {
                    return new AuthenticationResult(true, "Connected (no authentication requested).", greeting ?? string.Empty, lastResponse);
                }

                // Send AUTHINFO USER
                await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                string? resp1 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = resp1 ?? string.Empty;

                if (resp1 == null)
                {
                    return new AuthenticationResult(false, "No response after AUTHINFO USER (timeout).", greeting ?? string.Empty, lastResponse);
                }

                // If server replies 281 -> already authenticated / accepted
                if (resp1.StartsWith("281"))
                {
                    return new AuthenticationResult(true, $"Authentication succeeded: {resp1}", greeting ?? string.Empty, resp1);
                }

                // If server replies 381 => password required -> send PASS
                if (resp1.StartsWith("381"))
                {
                    await writer.WriteLineAsync($"AUTHINFO PASS {password}").ConfigureAwait(false);
                    string? resp2 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    lastResponse = resp2 ?? string.Empty;

                    if (resp2 == null)
                    {
                        return new AuthenticationResult(false, "No response after AUTHINFO PASS (timeout).", greeting ?? string.Empty, lastResponse);
                    }

                    if (resp2.StartsWith("281"))
                    {
                        return new AuthenticationResult(true, $"Authentication succeeded: {resp2}", greeting ?? string.Empty, resp2);
                    }

                    return new AuthenticationResult(false, $"Authentication failed: {resp2}", greeting ?? string.Empty, resp2);
                }

                // Any other reply is considered a failure
                return new AuthenticationResult(false, $"Unexpected server reply after AUTHINFO USER: {resp1}", greeting ?? string.Empty, resp1);
            }
            catch (Exception ex)
            {
                return new AuthenticationResult(false, $"Exception: {ex.Message}", string.Empty, string.Empty);
            }
            finally
            {
                if (client != null)
                {
                    try { client.Close(); } catch { }
                    client.Dispose();
                }
            }
        }

        // Helper: read a line with timeout. Returns null on timeout.
        private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, int timeoutMs)
        {
            Task<string?> readTask = reader.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != readTask) return null;
            return await readTask.ConfigureAwait(false);
        }
    }
}
