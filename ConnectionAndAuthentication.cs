using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Project_NNTP_Niklas
{
    /// <summary>
    /// Small helper that performs one‑shot NNTP connections and operations.
    /// The class contains methods that each open a TCP socket to the NNTP server,
    /// optionally perform AUTHINFO USER/PASS, issue a command (LIST, LISTGROUP, GROUP, HEAD, ...),
    /// read the server replies and close the socket before returning.
    /// 
    /// Important characteristics:
    /// - Each method is "one-shot": it opens a fresh TcpClient and disposes it in the finally block.
    /// - Methods use ASCII encoding (NNTP is an ASCII based protocol).
    /// - Read operations use a small custom timeout wrapper to avoid indefinite blocking.
    /// - Methods return small record types describing success, messages and any multiline data.
    /// </summary>
    public sealed class ConnectionAndAuthentication
    {
        // Compact result records used by callers to inspect success, human message,
        // server greeting, last single-line response and any returned content lines.
        public record AuthenticationResult(bool Success, string Message, string ServerGreeting, string LastResponse);
        public record ListGroupsResult(bool Success, string Message, string ServerGreeting, string LastResponse, string[]? Groups);
        public record GroupArticlesResult(bool Success, string Message, string ServerGreeting, string LastResponse, string[]? ArticleNumbers);
        public record ArticleHeadersResult(bool Success, string Message, string ServerGreeting, string LastResponse, string[]? Headers);

        /// <summary>
        /// Connects to an NNTP server and performs AUTHINFO USER / PASS if a username is supplied.
        /// This is a convenience, one-shot authentication check intended for UI flows that only
        /// need to verify credentials and read the server greeting.
        /// 
        /// Behavior details and protocol notes:
        /// - Establishes a TcpClient connection to the specified host/port.
        /// - Waits for the initial server greeting (single-line) and stores it as ServerGreeting.
        /// - If <paramref name="username"/> is null/empty the method returns success (no auth requested).
        /// - Sends "AUTHINFO USER {username}" and reads the single-line response.
        ///   - If the server responds with 281 -> authentication is accepted (already authenticated/accepted).
        ///   - If the server responds with 381 -> server requests a password; the method sends "AUTHINFO PASS {password}".
        ///   - If the PASS step returns 281 -> authentication succeeded; otherwise it's treated as failure.
        /// - On any read timeout or unexpected reply the method returns a failure AuthenticationResult
        ///   with LastResponse populated where possible.
        /// - The TcpClient and streams are always closed and disposed in the finally block, so no connection is kept alive.
        /// 
        /// Security note:
        /// - Password is sent in plain ASCII over the socket. If you need encryption use an NNTP-over-TLS port (commonly 563)
        ///   and consider validating the server certificate when using SslStream (not implemented here).
        /// 
        /// Timeouts:
        /// - The method accepts a timeoutMs parameter used to bound connect and read operations. Reads use the helper
        ///   ReadLineWithTimeoutAsync which returns null on timeout and the method converts that to an AuthenticationResult failure.
        /// </summary>
        public async Task<AuthenticationResult> AuthenticateAsync(string host, int port, string username, string password, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));
            if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));

            TcpClient? client = null;

            try
            {
                client = new TcpClient();

                // Connect with timeout: start connect and await either connect or Task.Delay(timeout)
                var connectTask = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != connectTask)
                {
                    // Timed out
                    return new AuthenticationResult(false, "Connection timed out.", string.Empty, string.Empty);
                }

                if (!client.Connected)
                {
                    // Very unlikely after ConnectAsync completed, but check defensively
                    return new AuthenticationResult(false, "Failed to connect.", string.Empty, string.Empty);
                }

                // Use the network stream for ASCII NNTP conversation.
                using NetworkStream ns = client.GetStream();

                // Synchronous read timeout (fallback) — primary reads use async wrapper with Task.WhenAny.
                ns.ReadTimeout = timeoutMs;

                // Create reader/writer over the same stream. leaveOpen: true so we control disposal via using statements.
                using var reader = new StreamReader(ns, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(ns, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true
                };

                // Read server greeting (single-line). Many servers reply "200 ..." or "201 ..." or similar.
                string? greeting = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                var lastResponse = greeting ?? string.Empty;

                // If the caller didn't request authentication, return success with the greeting for context.
                if (string.IsNullOrWhiteSpace(username))
                {
                    return new AuthenticationResult(true, "Connected (no authentication requested).", greeting ?? string.Empty, lastResponse);
                }

                // Send AUTHINFO USER <username>
                await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                string? resp1 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = resp1 ?? string.Empty;

                if (resp1 == null)
                {
                    // No single-line response (timeout)
                    return new AuthenticationResult(false, "No response after AUTHINFO USER (timeout).", greeting ?? string.Empty, lastResponse);
                }

                // 281 -> already authenticated or accepted
                if (resp1.StartsWith("281"))
                {
                    return new AuthenticationResult(true, $"Authentication succeeded: {resp1}", greeting ?? string.Empty, resp1);
                }

                // 381 -> password required
                if (resp1.StartsWith("381"))
                {
                    // Send AUTHINFO PASS <password>
                    await writer.WriteLineAsync($"AUTHINFO PASS {password}").ConfigureAwait(false);
                    string? resp2 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    lastResponse = resp2 ?? string.Empty;

                    if (resp2 == null)
                    {
                        return new AuthenticationResult(false, "No response after AUTHINFO PASS (timeout).", greeting ?? string.Empty, lastResponse);
                    }

                    if (resp2.StartsWith("281"))
                    {
                        // Auth succeeded
                        return new AuthenticationResult(true, $"Authentication succeeded: {resp2}", greeting ?? string.Empty, resp2);
                    }

                    // Auth failed (any other reply)
                    return new AuthenticationResult(false, $"Authentication failed: {resp2}", greeting ?? string.Empty, resp2);
                }

                // Any other reply is unexpected for AUTHINFO USER
                return new AuthenticationResult(false, $"Unexpected server reply after AUTHINFO USER: {resp1}", greeting ?? string.Empty, resp1);
            }
            catch (Exception ex)
            {
                // Exceptions (socket issues, unexpected IO errors) are converted to a failure result with message.
                return new AuthenticationResult(false, $"Exception: {ex.Message}", string.Empty, string.Empty);
            }
            finally
            {
                // Always try to close and dispose the client if created.
                if (client != null)
                {
                    try { client.Close(); } catch { }
                    client.Dispose();
                }
            }
        }

        /// <summary>
        /// Connects to the server, optionally authenticates, sends LIST and returns the group lines.
        /// This is a one-shot operation (connection is closed before the method returns).
        /// 
        /// Protocol specifics:
        /// - LIST typically returns "215 <info>" followed by a multiline body terminated by a single "." line.
        /// - If the server responds with 500/501/502 (command not implemented/recognized), attempt "LIST ACTIVE".
        /// - Lines may be dot-stuffed: lines that begin with "." are sent as ".." — the method strips one dot.
        /// 
        /// Returns:
        /// - ListGroupsResult.Success true with Groups[] containing the raw LIST lines (each line typically: "group high low posting")
        /// - On timeout, unexpected replies or exceptions the method returns Success = false and includes messages for diagnostics.
        /// </summary>
        public async Task<ListGroupsResult> GetNewsgroupsAsync(string host, int port, string? username = null, string? password = null, int timeoutMs = 5000)
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
                    return new ListGroupsResult(false, "Connection timed out.", string.Empty, string.Empty, null);
                }

                if (!client.Connected)
                {
                    return new ListGroupsResult(false, "Failed to connect.", string.Empty, string.Empty, null);
                }

                using NetworkStream ns = client.GetStream();
                // Synchronous fallback read timeout
                ns.ReadTimeout = timeoutMs;

                using var reader = new StreamReader(ns, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(ns, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true
                };

                // Initial server greeting
                string? greeting = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                var lastResponse = greeting ?? string.Empty;

                // Optional authentication like in AuthenticateAsync: send AUTHINFO USER/PASS if username provided.
                if (!string.IsNullOrWhiteSpace(username))
                {
                    await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                    string? resp1 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    lastResponse = resp1 ?? string.Empty;

                    if (resp1 == null)
                    {
                        return new ListGroupsResult(false, "No response after AUTHINFO USER (timeout).", greeting ?? string.Empty, lastResponse, null);
                    }

                    if (resp1.StartsWith("381"))
                    {
                        // Request password
                        await writer.WriteLineAsync($"AUTHINFO PASS {password ?? string.Empty}").ConfigureAwait(false);
                        string? resp2 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                        lastResponse = resp2 ?? string.Empty;

                        if (resp2 == null)
                        {
                            return new ListGroupsResult(false, "No response after AUTHINFO PASS (timeout).", greeting ?? string.Empty, lastResponse, null);
                        }

                        if (!resp2.StartsWith("281"))
                        {
                            return new ListGroupsResult(false, $"Authentication failed: {resp2}", greeting ?? string.Empty, resp2 ?? string.Empty, null);
                        }
                    }
                    else if (!resp1.StartsWith("281"))
                    {
                        // Unexpected reply after AUTHINFO USER
                        return new ListGroupsResult(false, $"Unexpected server reply after AUTHINFO USER: {resp1}", greeting ?? string.Empty, resp1, null);
                    }
                }

                // Send LIST; many servers return 215 with a multiline body.
                await writer.WriteLineAsync("LIST").ConfigureAwait(false);
                string? listResp = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = listResp ?? string.Empty;

                if (listResp == null)
                {
                    return new ListGroupsResult(false, "No response to LIST (timeout).", greeting ?? string.Empty, lastResponse, null);
                }

                if (!listResp.StartsWith("215"))
                {
                    // Some servers don't support bare LIST and return 500/501/502. Try "LIST ACTIVE" as fallback.
                    if (listResp.StartsWith("500") || listResp.StartsWith("501") || listResp.StartsWith("502"))
                    {
                        await writer.WriteLineAsync("LIST ACTIVE").ConfigureAwait(false);
                        listResp = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                        lastResponse = listResp ?? string.Empty;

                        if (listResp == null || !listResp.StartsWith("215"))
                        {
                            return new ListGroupsResult(false, $"LIST failed: {listResp}", greeting ?? string.Empty, lastResponse, null);
                        }
                    }
                    else
                    {
                        return new ListGroupsResult(false, $"LIST failed: {listResp}", greeting ?? string.Empty, lastResponse, null);
                    }
                }

                // Read the multiline body (terminated by a single dot on a line).
                var groups = new List<string>();
                while (true)
                {
                    var line = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    if (line == null)
                    {
                        return new ListGroupsResult(false, "Timeout while reading LIST body.", greeting ?? string.Empty, lastResponse, null);
                    }

                    if (line == ".") break;
                    // NNTP "dot-stuffing": lines starting with "." are sent as ".." — remove extra dot.
                    if (line.StartsWith("..")) line = line.Substring(1);
                    groups.Add(line);
                }

                return new ListGroupsResult(true, "LIST succeeded.", greeting ?? string.Empty, lastResponse, groups.ToArray());
            }
            catch (Exception ex)
            {
                // Convert unexpected exceptions to non-throwing failure result to simplify UI handling.
                return new ListGroupsResult(false, $"Exception: {ex.Message}", string.Empty, string.Empty, null);
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

        /// <summary>
        /// Connects, optionally authenticates, issues GROUP and LISTGROUP (or LISTGROUP fallback)
        /// and returns article numbers for the specified group. Implemented as a one-shot connection.
        /// 
        /// Protocol notes:
        /// - GROUP <group> typically returns "211 <count> <low> <high> <groupname>" on success.
        /// - LISTGROUP <group> returns a multiline response containing article numbers, terminated by ".".
        /// - If LISTGROUP fails for a particular server, additional fallbacks could be attempted (not implemented here).
        /// 
        /// Usage:
        /// - The caller may then call HEAD <number> or ARTICLE <number> with the returned article numbers.
        /// </summary>
        public async Task<GroupArticlesResult> GetArticlesForGroupAsync(string host, int port, string group, string? username = null, string? password = null, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));
            if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(group)) throw new ArgumentNullException(nameof(group));

            TcpClient? client = null;

            try
            {
                client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != connectTask)
                {
                    return new GroupArticlesResult(false, "Connection timed out.", string.Empty, string.Empty, null);
                }

                if (!client.Connected)
                {
                    return new GroupArticlesResult(false, "Failed to connect.", string.Empty, string.Empty, null);
                }

                using NetworkStream ns = client.GetStream();
                ns.ReadTimeout = timeoutMs;

                using var reader = new StreamReader(ns, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(ns, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true            
                };

                // Read greeting then optionally authenticate (same pattern used elsewhere).
                string? greeting = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                var lastResponse = greeting ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(username))
                {
                    await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                    string? resp1 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    lastResponse = resp1 ?? string.Empty;
                    if (resp1 == null)
                    {
                        return new GroupArticlesResult(false, "No response after AUTHINFO USER (timeout).", greeting ?? string.Empty, lastResponse, null);
                    }

                    if (resp1.StartsWith("381"))
                    {
                        await writer.WriteLineAsync($"AUTHINFO PASS {password ?? string.Empty}").ConfigureAwait(false);
                        string? resp2 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                        lastResponse = resp2 ?? string.Empty;
                        if (resp2 == null)
                        {
                            return new GroupArticlesResult(false, "No response after AUTHINFO PASS (timeout).", greeting ?? string.Empty, lastResponse, null);
                        }
                        if (!resp2.StartsWith("281"))
                        {
                            return new GroupArticlesResult(false, $"Authentication failed: {resp2}", greeting ?? string.Empty, resp2 ?? string.Empty, null);
                        }
                    }
                    else if (!resp1.StartsWith("281"))
                    {
                        return new GroupArticlesResult(false, $"Unexpected server reply after AUTHINFO USER: {resp1}", greeting ?? string.Empty, resp1 ?? string.Empty, null);
                    }
                }

                // Tell server which group we want
                await writer.WriteLineAsync($"GROUP {group}").ConfigureAwait(false);
                string? groupResp = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = groupResp ?? string.Empty;
                if (groupResp == null)
                {
                    return new GroupArticlesResult(false, "No response to GROUP (timeout).", greeting ?? string.Empty, lastResponse, null);
                }

                if (!groupResp.StartsWith("211"))
                {
                    // 211 expected: success for GROUP command
                    return new GroupArticlesResult(false, $"GROUP failed: {groupResp}", greeting ?? string.Empty, lastResponse, null);
                }

                // Try LISTGROUP for a multiline list of article numbers
                await writer.WriteLineAsync($"LISTGROUP {group}").ConfigureAwait(false);
                string? listResp = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = listResp ?? string.Empty;

                if (listResp == null)
                {
                    return new GroupArticlesResult(false, "No response to LISTGROUP (timeout).", greeting ?? string.Empty, lastResponse, null);
                }

                // Servers may return 211 or 215 depending on implementation; accept both as start of multiline
                if (!listResp.StartsWith("211") && !listResp.StartsWith("215"))
                {
                    return new GroupArticlesResult(false, $"LISTGROUP failed: {listResp}", greeting ?? string.Empty, lastResponse, null);
                }

                var numbers = new List<string>();
                while (true)
                {
                    var line = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    if (line == null)
                    {
                        return new GroupArticlesResult(false, "Timeout while reading LISTGROUP body.", greeting ?? string.Empty, lastResponse, null);
                    }
                    if (line == ".") break;
                    if (line.StartsWith("..")) line = line.Substring(1);
                    if (!string.IsNullOrWhiteSpace(line)) numbers.Add(line.Trim());
                }

                return new GroupArticlesResult(true, "LISTGROUP succeeded.", greeting ?? string.Empty, lastResponse, numbers.ToArray());
            }
            catch (Exception ex)
            {
                return new GroupArticlesResult(false, $"Exception: {ex.Message}", string.Empty, string.Empty, null);
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

        /// <summary>
        /// Retrieve article headers for a numeric article id (one-shot connection).
        /// Uses "HEAD <number>" and returns the header lines (multiline terminated by ".").
        /// 
        /// Success conditions:
        /// - Server replies with 221 to indicate start of header block.
        /// - The method collects lines until the terminating "." and returns them as Headers[].
        /// 
        /// Errors:
        /// - Timeouts, unexpected replies, or exceptions are returned as a failure ArticleHeadersResult.
        /// </summary>
        public async Task<ArticleHeadersResult> GetArticleHeadersAsync(string host, int port, string articleNumber, string? username = null, string? password = null, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));
            if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(articleNumber)) throw new ArgumentNullException(nameof(articleNumber));

            TcpClient? client = null;

            try
            {
                client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != connectTask)
                {
                    return new ArticleHeadersResult(false, "Connection timed out.", string.Empty, string.Empty, null);
                }

                if (!client.Connected)
                {
                    return new ArticleHeadersResult(false, "Failed to connect.", string.Empty, string.Empty, null);
                }

                using NetworkStream ns = client.GetStream();
                ns.ReadTimeout = timeoutMs;

                using var reader = new StreamReader(ns, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(ns, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true
                };

                // Greeting
                string? greeting = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                var lastResponse = greeting ?? string.Empty;

                // Optional authentication
                if (!string.IsNullOrWhiteSpace(username))
                {
                    await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                    string? resp1 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    lastResponse = resp1 ?? string.Empty;
                    if (resp1 == null)
                    {
                        return new ArticleHeadersResult(false, "No response after AUTHINFO USER (timeout).", greeting ?? string.Empty, lastResponse, null);
                    }

                    if (resp1.StartsWith("381"))
                    {
                        await writer.WriteLineAsync($"AUTHINFO PASS {password ?? string.Empty}").ConfigureAwait(false);
                        string? resp2 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                        lastResponse = resp2 ?? string.Empty;
                        if (resp2 == null)
                        {
                            return new ArticleHeadersResult(false, "No response after AUTHINFO PASS (timeout).", greeting ?? string.Empty, lastResponse, null);
                        }
                        if (!resp2.StartsWith("281"))
                        {
                            return new ArticleHeadersResult(false, $"Authentication failed: {resp2}", greeting ?? string.Empty, resp2 ?? string.Empty, null);
                        }
                    }
                    else if (!resp1.StartsWith("281"))
                    {
                        return new ArticleHeadersResult(false, $"Unexpected server reply after AUTHINFO USER: {resp1}", greeting ?? string.Empty, resp1 ?? string.Empty, null);
                    }
                }

                // Send HEAD <articleNumber>
                await writer.WriteLineAsync($"HEAD {articleNumber}").ConfigureAwait(false);
                string? headResp = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = headResp ?? string.Empty;

                if (headResp == null)
                {
                    return new ArticleHeadersResult(false, "No response to HEAD (timeout).", greeting ?? string.Empty, lastResponse, null);
                }

                if (!headResp.StartsWith("221"))
                {
                    return new ArticleHeadersResult(false, $"HEAD failed: {headResp}", greeting ?? string.Empty, lastResponse, null);
                }

                var headers = new List<string>();
                while (true)
                {
                    var line = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    if (line == null)
                    {
                        return new ArticleHeadersResult(false, "Timeout while reading HEAD body.", greeting ?? string.Empty, lastResponse, null);
                    }
                    if (line == ".") break;
                    if (line.StartsWith("..")) line = line.Substring(1);
                    headers.Add(line);
                }

                return new ArticleHeadersResult(true, "HEAD succeeded.", greeting ?? string.Empty, lastResponse, headers.ToArray());
            }
            catch (Exception ex)
            {
                return new ArticleHeadersResult(false, $"Exception: {ex.Message}", string.Empty, string.Empty, null);
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

        /// <summary>
        /// Helper that reads a single line from the StreamReader with an upper bound specified by timeoutMs.
        /// Returns the line (without newline) or null when the timeout elapses before a line was read.
        /// 
        /// Implementation note:
        /// - Uses Task.WhenAny to race the ReadLineAsync against Task.Delay(timeoutMs).
        /// - Caller must interpret null as a timeout and handle it appropriately (typically treat as failure).
        /// </summary>
        private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, int timeoutMs)
        {
            Task<string?> readTask = reader.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != readTask) return null;
            return await readTask.ConfigureAwait(false);
        }
    }
}
