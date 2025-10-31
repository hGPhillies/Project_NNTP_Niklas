using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Project_NNTP_Niklas
{
    
    public sealed class ConnectionAndAuthentication
    {
      
        public record AuthenticationResult(bool Success, string Message, string ServerGreeting, string LastResponse);
        public record ListGroupsResult(bool Success, string Message, string ServerGreeting, string LastResponse, string[]? Groups);
        public record GroupArticlesResult(bool Success, string Message, string ServerGreeting, string LastResponse, string[]? ArticleNumbers);
        public record ArticleHeadersResult(bool Success, string Message, string ServerGreeting, string LastResponse, string[]? Headers);
        public record ArticleBodyResult(bool Success, string Message, string ServerGreeting, string LastResponse, string[]? Body);

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
                    // Timed out
                    return new AuthenticationResult(false, "Connection timed out.", string.Empty, string.Empty);
                }

                if (!client.Connected)
                {
 
                    return new AuthenticationResult(false, "Failed to connect.", string.Empty, string.Empty);
                }

                using NetworkStream ns = client.GetStream();


                ns.ReadTimeout = timeoutMs;

     
                using var reader = new StreamReader(ns, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(ns, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true
                };


                string? greeting = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                var lastResponse = greeting ?? string.Empty;


                if (string.IsNullOrWhiteSpace(username))
                {
                    return new AuthenticationResult(true, "Connected (no authentication requested).", greeting ?? string.Empty, lastResponse);
                }


                await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                string? resp1 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = resp1 ?? string.Empty;

                if (resp1 == null)
                {

                    return new AuthenticationResult(false, "No response after AUTHINFO USER (timeout).", greeting ?? string.Empty, lastResponse);
                }

                if (resp1.StartsWith("281"))
                {
                    return new AuthenticationResult(true, $"Authentication succeeded: {resp1}", greeting ?? string.Empty, resp1);
                }


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




       
        public async Task<ListGroupsResult> NewsgroupService(string host, int port, string? username = null, string? password = null, int timeoutMs = 5000)
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

                // Send LIST;
                await writer.WriteLineAsync("LIST").ConfigureAwait(false);
                string? listResp = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = listResp ?? string.Empty;

                if (listResp == null)
                {
                    return new ListGroupsResult(false, "No response to LIST (timeout).", greeting ?? string.Empty, lastResponse, null);
                }

                if (!listResp.StartsWith("215"))
                {
                    // Some servers don't support bare LIST and return 500/501/502. 
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

                // Read the multiline body 
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

       
        public async Task<GroupArticlesResult>  GetArticlesForGroupAsync(string host, int port, string group, string? username = null, string? password = null, int timeoutMs = 5000)
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

                // Send HEAD 
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

       
        public async Task<ArticleBodyResult> GetArticleAsync(string host, int port, string articleNumber, string? username = null, string? password = null, string? group = null, int timeoutMs = 5000)
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
                    return new ArticleBodyResult(false, "Connection timed out.", string.Empty, string.Empty, null);
                }

                if (!client.Connected)
                {
                    return new ArticleBodyResult(false, "Failed to connect.", string.Empty, string.Empty, null);
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

                // Optional auth
                if (!string.IsNullOrWhiteSpace(username))
                {
                    await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                    string? resp1 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    lastResponse = resp1 ?? string.Empty;
                    if (resp1 == null)
                    {
                        return new ArticleBodyResult(false, "No response after AUTHINFO USER (timeout).", greeting ?? string.Empty, lastResponse, null);
                    }

                    if (resp1.StartsWith("381"))
                    {
                        await writer.WriteLineAsync($"AUTHINFO PASS {password ?? string.Empty}").ConfigureAwait(false);
                        string? resp2 = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                        lastResponse = resp2 ?? string.Empty;
                        if (resp2 == null)
                        {
                            return new ArticleBodyResult(false, "No response after AUTHINFO PASS (timeout).", greeting ?? string.Empty, lastResponse, null);
                        }
                        if (!resp2.StartsWith("281"))
                        {
                            return new ArticleBodyResult(false, $"Authentication failed: {resp2}", greeting ?? string.Empty, resp2 ?? string.Empty, null);
                        }
                    }
                    else if (!resp1.StartsWith("281"))
                    {
                        return new ArticleBodyResult(false, $"Unexpected server reply after AUTHINFO USER: {resp1}", greeting ?? string.Empty, resp1 ?? string.Empty, null);
                    }
                }

                // If caller supplied a group, select it in this session — necessary when using numeric article IDs.
                if (!string.IsNullOrWhiteSpace(group))
                {
                    await writer.WriteLineAsync($"GROUP {group}").ConfigureAwait(false);
                    string? groupResp = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    lastResponse = groupResp ?? lastResponse;
                    if (groupResp == null)
                    {
                        return new ArticleBodyResult(false, "No response to GROUP (timeout).", greeting ?? string.Empty, lastResponse, null);
                    }

                    if (!groupResp.StartsWith("211"))
                    {
                        return new ArticleBodyResult(false, $"GROUP failed: {groupResp}", greeting ?? string.Empty, lastResponse, null);
                    }
                }

                // ARTICLE <articleNumber>
                await writer.WriteLineAsync($"ARTICLE {articleNumber}").ConfigureAwait(false);
                string? artResp = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                lastResponse = artResp ?? lastResponse;

                if (artResp == null)
                {
                    return new ArticleBodyResult(false, "No response to ARTICLE (timeout).", greeting ?? string.Empty, lastResponse, null);
                }

                // 220 indicates start of article
                if (!artResp.StartsWith("220"))
                {
                    return new ArticleBodyResult(false, $"ARTICLE failed: {artResp}", greeting ?? string.Empty, lastResponse, null);
                }

                var body = new List<string>();
                while (true)
                {
                    var line = await ReadLineWithTimeoutAsync(reader, timeoutMs).ConfigureAwait(false);
                    if (line == null)
                    {
                        return new ArticleBodyResult(false, "Timeout while reading ARTICLE body.", greeting ?? string.Empty, lastResponse, null);
                    }
                    if (line == ".") break;
                    if (line.StartsWith("..")) line = line.Substring(1);
                    body.Add(line);
                }

                return new ArticleBodyResult(true, "ARTICLE succeeded.", greeting ?? string.Empty, lastResponse, body.ToArray());
            }
            catch (Exception ex)
            {
                return new ArticleBodyResult(false, $"Exception: {ex.Message}", string.Empty, string.Empty, null);
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

       
        private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, int timeoutMs)
        {
            Task<string?> readTask = reader.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != readTask) return null;
            return await readTask.ConfigureAwait(false);
        }
    }
}
