﻿/*
* Copyright © 2017-Present Jesse Nicholson
* This Source Code Form is subject to the terms of the Mozilla Public
* License, v. 2.0. If a copy of the MPL was not distributed with this
* file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

using CitadelCore.Extensions;
using CitadelCore.IO;
using CitadelCore.Logging;
using CitadelCore.Net.Http;
using CitadelCore.Net.Proxy;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CitadelCore.Net.Handlers
{   
    /// <summary>
    /// The FilterHttpResponse handler is designed to proxy HTTP requests and responses, while
    /// providing an opportunity for users to inspect and optionally filter and modifiy requests and
    /// responses at different stages of the transaction.
    /// </summary>
    internal class FilterHttpResponseHandler : AbstractFilterResponseHandler
    {
        /// <summary>
        /// We pass this in to stream copy operations whenever the user has asked us to pull
        /// a payload from the net into memory. We set a hard limit of ~128 megs simply to
        /// avoid being vulnerable to an attack that would balloon memory consumption.
        /// </summary>
        private static readonly long s_maxInMemoryData = 128000000;

        private static HttpClient s_client;

        private static readonly Regex s_httpVerRegex = new Regex("([0-9]+\\.[0-9]+)", RegexOptions.Compiled | RegexOptions.ECMAScript);

        static FilterHttpResponseHandler()
        {
            // Enforce global use of good/strong TLS protocols.
            ServicePointManager.SecurityProtocol = (ServicePointManager.SecurityProtocol & ~SecurityProtocolType.Ssl3) | (SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12);

            // If this isn't set, we'll have a massive bottlenet on our upstream flow. The
            // performance gains here extreme. This must be set.
            ServicePointManager.DefaultConnectionLimit = ushort.MaxValue;

            ServicePointManager.Expect100Continue = false;
            ServicePointManager.CheckCertificateRevocationList = true;
            ServicePointManager.ReusePort = true;
            ServicePointManager.UseNagleAlgorithm = false;
            
            // We need UseCookies set to false here. We then need to set per-request cookies by
            // manually adding the "Cookie" header. If we don't have UseCookies set to false here,
            // this will not work.
            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
                ClientCertificateOptions = ClientCertificateOption.Automatic,
                AllowAutoRedirect = false,
                Proxy = null
            };
            
            s_client = new HttpClient(handler);
        }

        /// <summary>
        /// Constructs a FilterHttpResponseHandler instance.
        /// </summary>
        /// <param name="newMessageCallback">
        /// Callback used for new messages.
        /// </param>
        /// <param name="wholeBodyInspectionCallback">
        /// Callback used when full-body content inspection is requested on a new message.
        /// </param>
        /// <param name="streamInspectionCallback">
        /// Callback used when streamed content inspection is requested on a new message.
        /// </param>
        /// <param name="badCertificateCallback">
        /// Callback used when a bad certificate is encountered.
        /// </param>
        public FilterHttpResponseHandler(NewHttpMessageHandler newMessageCallback,
            HttpMessageWholeBodyInspectionHandler wholeBodyInspectionCallback,
            HttpMessageStreamedInspectionHandler streamInspectionCallback,
            BadCertificateHandler badCertificateCallback) : base(newMessageCallback, wholeBodyInspectionCallback, streamInspectionCallback, badCertificateCallback)
        {

        }

        /// <summary>
        /// Invoked when this handler is determined to be the best suited to handle the supplied connection.
        /// </summary>
        /// <param name="context">
        /// The HTTP context.
        /// </param>
        /// <returns>
        /// The handling task.
        /// </returns>
        public override async Task Handle(HttpContext context)
        {
            Diagnostics.DiagnosticsWebSession diagSession = new Diagnostics.DiagnosticsWebSession();

            if(Diagnostics.Collector.IsDiagnosticsEnabled)
            {
                diagSession.DateStarted = DateTime.Now;
            }

            try
            {   
                // Use helper to get the full, proper URL for the request.
                //var fullUrl = Microsoft.AspNetCore.Http.Extensions.UriHelper.GetDisplayUrl(context.Request);
                var fullUrl = Microsoft.AspNetCore.Http.Extensions.UriHelper.GetEncodedUrl(context.Request);

                // Next we need to try and parse the URL as a URI, because the websocket client
                // requires this for connecting upstream.

                if (!Uri.TryCreate(fullUrl, UriKind.RelativeOrAbsolute, out Uri reqUrl))
                {
                    LoggerProxy.Default.Error("Failed to parse HTTP URL.");
                    return;
                }

                if (context.Connection.ClientCertificate != null)
                {
                    // TODO - Handle client certificates.
                }

                bool requestHasZeroContentLength = false;

                foreach (var hdr in context.Request.Headers)
                {
                    try
                    {
                        if (hdr.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && hdr.Value.ToString().Equals("0"))
                        {
                            requestHasZeroContentLength = true;
                        }
                    }
                    catch { }
                }

                HttpRequestMessage requestMsg;
                diagSession.ClientRequestUri = fullUrl;

                // Let's do our first call to message begin for the request side.                
                var requestMessageNfo = new HttpMessageInfo
                {
                    Url = reqUrl,
                    Method = new HttpMethod(context.Request.Method),
                    IsEncrypted = context.Request.IsHttps,
                    Headers = context.Request.Headers.ToNameValueCollection(),
                    MessageProtocol = MessageProtocol.Http,
                    MessageType = MessageType.Request,
                    RemoteAddress = context.Connection.RemoteIpAddress,
                    RemotePort = (ushort)context.Connection.RemotePort,
                    LocalAddress = context.Connection.LocalIpAddress,
                    LocalPort = (ushort)context.Connection.LocalPort
                };

                _newMessageCb?.Invoke(requestMessageNfo);

                if (Diagnostics.Collector.IsDiagnosticsEnabled)
                {
                    diagSession.ClientRequestHeaders = context.Request.Headers.ToString();
                }

                if (requestMessageNfo.ProxyNextAction == ProxyNextAction.DropConnection)
                {
                    // Apply whatever the user did here and then quit.
                    context.Response.ClearAllHeaders();
                    await context.Response.ApplyMessageInfo(requestMessageNfo, context.RequestAborted);
                    return;
                }

                // Create the message AFTER we give the user a chance to alter things.
                requestMsg = new HttpRequestMessage(requestMessageNfo.Method, requestMessageNfo.Url);
                var initialFailedHeaders = requestMsg.PopulateHeaders(requestMessageNfo.Headers);

                // Check if we have a request body.
                if (context.Request.Body != null)
                {
                    // Now check what the user wanted to do.
                    switch (requestMessageNfo.ProxyNextAction)
                    {
                        // We have a body and the user previously instructed us to give them the
                        // content, if any, for inspection.
                        case ProxyNextAction.AllowButRequestContentInspection:
                            {
                                // Get the request body into memory.
                                using (var ms = new MemoryStream())
                                {
                                    await Microsoft.AspNetCore.Http.Extensions.StreamCopyOperation.CopyToAsync(context.Request.Body, ms, s_maxInMemoryData, context.RequestAborted);

                                    var requestBody = ms.ToArray();

                                    // If we don't have a body, there's no sense in calling the message end callback.
                                    if (requestBody.Length > 0)
                                    {

                                        diagSession.ClientRequestBody = requestBody;

                                        // We'll now call the message end function for the request side.
                                        requestMessageNfo = new HttpMessageInfo
                                        {
                                            Url = reqUrl,
                                            Method = new HttpMethod(context.Request.Method),
                                            IsEncrypted = context.Request.IsHttps,
                                            Headers = context.Request.Headers.ToNameValueCollection(),
                                            MessageProtocol = MessageProtocol.Http,
                                            MessageType = MessageType.Request,
                                            RemoteAddress = context.Connection.RemoteIpAddress,
                                            RemotePort = (ushort)context.Connection.RemotePort,
                                            LocalAddress = context.Connection.LocalIpAddress,
                                            LocalPort = (ushort)context.Connection.LocalPort,
                                            BodyInternal = requestBody
                                        };

                                        _wholeBodyInspectionCb?.Invoke(requestMessageNfo);

                                        if (requestMessageNfo.ProxyNextAction == ProxyNextAction.DropConnection)
                                        {
                                            // User wants to block this request after inspecting the content.
                                            // Apply whatever the user did here and then quit.
                                            context.Response.ClearAllHeaders();
                                            await context.Response.ApplyMessageInfo(requestMessageNfo, context.RequestAborted);

                                            return;
                                        }

                                        // Since the user may have modified things, we'll now re-create
                                        // the request no matter what.
                                        requestMsg = new HttpRequestMessage(requestMessageNfo.Method, requestMessageNfo.Url);
                                        initialFailedHeaders = requestMsg.PopulateHeaders(requestMessageNfo.Headers);

                                        // Set our content, even if it's empty. Don't worry about ByteArrayContent
                                        // and friends setting other headers, we're gonna blow relevant headers away
                                        // below and then set them properly.
                                        requestMsg.Content = new ByteArrayContent(requestBody);
                                        requestMsg.Content.Headers.Clear();

                                        requestMsg.Content.Headers.TryAddWithoutValidation("Content-Length", requestBody.Length.ToString());
                                    }
                                    else
                                    {
                                        if (requestHasZeroContentLength)
                                        {
                                            requestMsg.Content = new ByteArrayContent(requestBody);
                                            requestMsg.Content.Headers.Clear();
                                            requestMsg.Content.Headers.TryAddWithoutValidation("Content-Length", "0");
                                        }
                                    }
                                }
                            }
                            break;

                        case ProxyNextAction.AllowButRequestStreamedContentInspection:
                            {
                                requestMessageNfo = new HttpMessageInfo
                                {
                                    Url = reqUrl,
                                    Method = new HttpMethod(context.Request.Method),
                                    IsEncrypted = context.Request.IsHttps,
                                    Headers = context.Request.Headers.ToNameValueCollection(),
                                    MessageProtocol = MessageProtocol.Http,
                                    MessageType = MessageType.Request,
                                    RemoteAddress = context.Connection.RemoteIpAddress,
                                    RemotePort = (ushort)context.Connection.RemotePort,
                                    LocalAddress = context.Connection.LocalIpAddress,
                                    LocalPort = (ushort)context.Connection.LocalPort
                                };

                                // We have a body and the user wants to just stream-inspect it.
                                var wrappedStream = new InspectionStream(requestMessageNfo, context.Request.Body)
                                {
                                    StreamRead = OnWrappedStreamRead,
                                    StreamWrite = OnWrappedStreamWrite
                                };

                                requestMsg.Content = new StreamContent(wrappedStream);
                            }
                            break;

                        default:
                            {
                                if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > 0)
                                {
                                    // We have a body, but the user doesn't want to inspect it.
                                    // So, we'll just set our content to wrap the context's input
                                    // stream.
                                    requestMsg.Content = new StreamContent(context.Request.Body);
                                }
                                
                            }
                            break;
                    }                    
                }

                // Ensure that content type is set properly because ByteArrayContent and friends will
                // modify these fields.
                // To explain these further, these headers almost always fail because
                // they apply to the .Content property only (content-specific headers),
                // so once we have a .Content property created, we'll go ahead and
                // pour over the failed headers and try to apply to them to the content.
                initialFailedHeaders = requestMsg.PopulateHeaders(initialFailedHeaders);
#if VERBOSE_WARNINGS
                foreach (string key in initialFailedHeaders)
                {
                    LoggerProxy.Default.Warn(string.Format("Failed to add HTTP header with key {0} and with value {1}.", key, initialFailedHeaders[key]));
                }
#endif

                if(Diagnostics.Collector.IsDiagnosticsEnabled)
                {
                    diagSession.ServerRequestHeaders = requestMsg.Headers.ToString();
                }

                // Lets start sending the request upstream. We're going to ask the client to return
                // control to us when the headers are complete. This way we're not buffering entire
                // responses into memory, and if the user doesn't request to inspect the content, we
                // can just async stream the content transparently and Kestrel is so cool and sweet
                // and nice, it'll automatically stream as chunked content.
                HttpResponseMessage response = null;

                try
                {
                    response = await s_client.SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                    diagSession.StatusCode = (int)(response?.StatusCode ?? 0);
                }
                catch (HttpRequestException e)
                {
                    LoggerProxy.Default.Error(e);

                    if(e.InnerException is WebException && e.InnerException.InnerException is System.Security.Authentication.AuthenticationException)
                    {
                        requestMessageNfo = new HttpMessageInfo
                        {
                            Url = reqUrl,
                            Method = new HttpMethod(context.Request.Method),
                            IsEncrypted = context.Request.IsHttps,
                            Headers = context.Request.Headers.ToNameValueCollection(),
                            MessageProtocol = MessageProtocol.Http,
                            MessageType = MessageType.Request,
                            RemoteAddress = context.Connection.RemoteIpAddress,
                            RemotePort = (ushort)context.Connection.RemotePort,
                            LocalAddress = context.Connection.LocalIpAddress,
                            LocalPort = (ushort)context.Connection.LocalPort,
                            BodyInternal = null
                        };

                        _badCertificateCb?.Invoke(requestMessageNfo);

                        context.Response.ClearAllHeaders();
                        await context.Response.ApplyMessageInfo(requestMessageNfo, context.RequestAborted);

                        return;
                    }
                    else if(e.InnerException is WebException)
                    {
                        var webException = e.InnerException as WebException;

                        if(webException.Response != null)
                        {
                            diagSession.StatusCode = (int?)(webException.Response as HttpWebResponse)?.StatusCode ?? 0;
                        }
                    }
                }
                catch (Exception e)
                {
                    LoggerProxy.Default.Error(e);
                }

                if (response == null)
                {
                    return;
                }

                // Blow away all response headers. We wanna clone these now from our upstream request.
                context.Response.ClearAllHeaders();

                // Ensure our client's response status code is set to match ours.
                context.Response.StatusCode = (int)response.StatusCode;

                var responseHeaders = response.ExportAllHeaders();

                bool responseHasZeroContentLength = false;
                bool responseIsFixedLength = false;

                foreach (var kvp in responseHeaders.ToIHeaderDictionary())
                {
                    foreach (var value in kvp.Value)
                    {
                        if (kvp.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            responseIsFixedLength = true;

                            if (value.Length <= 0 && value.Equals("0"))
                            {
                                responseHasZeroContentLength = true;
                            }
                        }
                    }
                }

                if (Diagnostics.Collector.IsDiagnosticsEnabled)
                {
                    diagSession.ServerResponseHeaders = responseHeaders.ToString();
                }

                // Match the HTTP version of the client on the upstream request. We don't want to
                // transparently pass around headers that are wrong for the client's HTTP version.
                Version upstreamReqVersionMatch = null;

                Match match = s_httpVerRegex.Match(context.Request.Protocol);
                if (match != null && match.Success)
                {
                    upstreamReqVersionMatch = Version.Parse(match.Value);
                    requestMsg.Version = upstreamReqVersionMatch;
                }

                // For later reference...
                bool upstreamIsHttp1 = upstreamReqVersionMatch != null && upstreamReqVersionMatch.Major == 1 && upstreamReqVersionMatch.Minor == 0;

                // Let's call the message begin handler for the response. Unless of course, the user has asked us NOT to do this.
                if (requestMessageNfo.ProxyNextAction != ProxyNextAction.AllowAndIgnoreContentAndResponse)
                {   
                    var responseMessageNfo = new HttpMessageInfo
                    {
                        Url = reqUrl,
                        IsEncrypted = context.Request.IsHttps,
                        Headers = response.ExportAllHeaders(),
                        MessageProtocol = MessageProtocol.Http,
                        MessageType = MessageType.Response,
                        RemoteAddress = context.Connection.RemoteIpAddress,
                        RemotePort = (ushort)context.Connection.RemotePort,
                        LocalAddress = context.Connection.LocalIpAddress,
                        LocalPort = (ushort)context.Connection.LocalPort
                    };

                    _newMessageCb?.Invoke(responseMessageNfo);

                    if (responseMessageNfo.ProxyNextAction == ProxyNextAction.DropConnection)
                    {
                        // Apply whatever the user did here and then quit.
                        context.Response.ClearAllHeaders();
                        await context.Response.ApplyMessageInfo(responseMessageNfo, context.RequestAborted);

                        return;
                    }

                    context.Response.ClearAllHeaders();
                    context.Response.PopulateHeaders(responseMessageNfo.Headers);

                    switch (responseMessageNfo.ProxyNextAction)
                    {
                        case ProxyNextAction.AllowButRequestContentInspection:
                            {
                                using (var upstreamResponseStream = await response.Content.ReadAsStreamAsync())
                                {
                                    using (var ms = new MemoryStream())
                                    {
                                        await Microsoft.AspNetCore.Http.Extensions.StreamCopyOperation.CopyToAsync(upstreamResponseStream, ms, s_maxInMemoryData, context.RequestAborted);

                                        var responseBody = ms.ToArray();
                                        diagSession.ServerResponseBody = responseBody;

                                        responseMessageNfo = new HttpMessageInfo
                                        {
                                            Url = reqUrl,
                                            IsEncrypted = context.Request.IsHttps,
                                            Headers = response.ExportAllHeaders(),
                                            MessageProtocol = MessageProtocol.Http,
                                            MessageType = MessageType.Response,
                                            RemoteAddress = context.Connection.RemoteIpAddress,
                                            RemotePort = (ushort)context.Connection.RemotePort,
                                            LocalAddress = context.Connection.LocalIpAddress,
                                            LocalPort = (ushort)context.Connection.LocalPort,
                                            BodyInternal = responseBody
                                        };

                                        _wholeBodyInspectionCb?.Invoke(responseMessageNfo);

                                        if (responseMessageNfo.ProxyNextAction == ProxyNextAction.DropConnection)
                                        {
                                            // Apply whatever the user did here and then quit.
                                            context.Response.ClearAllHeaders();
                                            await context.Response.ApplyMessageInfo(responseMessageNfo, context.RequestAborted);

                                            return;
                                        }

                                        context.Response.ClearAllHeaders();
                                        context.Response.PopulateHeaders(responseMessageNfo.Headers);

                                        // User inspected but allowed the content. Just write to the response
                                        // body and then move on with your life fam.
                                        //
                                        // However, don't try to write a body if it's zero length. Also, do
                                        // not try to write a body, even if present, if the status is 204.
                                        // Kestrel will not let us do this, and so far I can't find a way to
                                        // remove this technically correct strict-compliance.
                                        if (!responseHasZeroContentLength && (responseBody.Length > 0 && context.Response.StatusCode != 204))
                                        {
                                            // If the request is HTTP1.0, we need to pull all the data so we
                                            // can properly set the content-length by adding the header in.
                                            if (upstreamIsHttp1)
                                            {
                                                context.Response.Headers.Add("Content-Length", responseBody.Length.ToString());
                                            }

                                            await context.Response.Body.WriteAsync(responseBody, 0, responseBody.Length);
                                        }
                                        else
                                        {
                                            if (responseHasZeroContentLength)
                                            {
                                                context.Response.Headers.Add("Content-Length", "0");
                                            }
                                        }

                                        // Ensure we exit here, because if we fall past this scope then the
                                        // response is going to get mangled.
                                        return;
                                    }
                                }
                            }                            

                        case ProxyNextAction.AllowButRequestStreamedContentInspection:
                            {
                                responseMessageNfo = new HttpMessageInfo
                                {
                                    Url = reqUrl,
                                    IsEncrypted = context.Request.IsHttps,
                                    Headers = response.ExportAllHeaders(),
                                    MessageProtocol = MessageProtocol.Http,
                                    MessageType = MessageType.Response,
                                    RemoteAddress = context.Connection.RemoteIpAddress,
                                    RemotePort = (ushort)context.Connection.RemotePort,
                                    LocalAddress = context.Connection.LocalIpAddress,
                                    LocalPort = (ushort)context.Connection.LocalPort
                                };

                                using (var responseStream = await response.Content.ReadAsStreamAsync())
                                {
                                    // We have a body and the user wants to just stream-inspect it.
                                    using (var wrappedStream = new InspectionStream(responseMessageNfo, responseStream))
                                    {
                                        wrappedStream.StreamRead = OnWrappedStreamRead;
                                        wrappedStream.StreamWrite = OnWrappedStreamWrite;

                                        await Microsoft.AspNetCore.Http.Extensions.StreamCopyOperation.CopyToAsync(wrappedStream, context.Response.Body, null, context.RequestAborted);
                                    }
                                }

                                return;
                            }
                    }
                    
                } // if (requestMessageNfo.ProxyNextAction != ProxyNextAction.AllowAndIgnoreContentAndResponse)                


                // If we made it here, then the user just wants to let the response be streamed in
                // without any inspection etc, so do exactly that.
                using (var responseStream = await response.Content.ReadAsStreamAsync())
                {
                    context.Response.StatusCode = (int)response.StatusCode;
                    context.Response.PopulateHeaders(response.ExportAllHeaders());

                    if (!responseHasZeroContentLength && (upstreamIsHttp1 || responseIsFixedLength))
                    {
                        using (var ms = new MemoryStream())
                        {
                            await Microsoft.AspNetCore.Http.Extensions.StreamCopyOperation.CopyToAsync(responseStream, ms, s_maxInMemoryData, context.RequestAborted);

                            var responseBody = ms.ToArray();

                            context.Response.Headers.Remove("Content-Length");

                            context.Response.Headers.Add("Content-Length", responseBody.Length.ToString());

                            await context.Response.Body.WriteAsync(responseBody, 0, responseBody.Length);
                        }
                    }
                    else
                    {
                        context.Response.Headers.Remove("Content-Length");

                        if (responseHasZeroContentLength)
                        {
                            context.Response.Headers.Add("Content-Length", "0");
                        }
                        else
                        {
                            await Microsoft.AspNetCore.Http.Extensions.StreamCopyOperation.CopyToAsync(responseStream, context.Response.Body, null, context.RequestAborted);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!(e is TaskCanceledException) && !(e is OperationCanceledException))
                {
                    // Ignore task cancelled exceptions.
                    LoggerProxy.Default.Error(e);
                }
            }
            finally
            {
                if(Diagnostics.Collector.IsDiagnosticsEnabled)
                {
                    diagSession.DateEnded = DateTime.Now;
                    Diagnostics.Collector.ReportSession(diagSession);
                }
            }
        }

        /// <summary>
        /// Handler for every requested InspectionStream object's read callback.
        /// </summary>
        /// <param name="stream">
        /// The originating stream.
        /// </param>
        /// <param name="buffer">
        /// The data passed through the stream.
        /// </param>
        /// <param name="dropConnection">
        /// Whether or not to immediately terminate the stream.
        /// </param>
        private void OnWrappedStreamRead(InspectionStream stream, Memory<byte> buffer, out bool dropConnection)
        {
            dropConnection = false;
            _streamInpsectionCb?.Invoke(stream.MessageInfo, StreamOperation.Read, buffer, out dropConnection);
        }

        /// <summary>
        /// Handler for every requested InspectionStream object's write callback.
        /// </summary>
        /// <param name="stream">
        /// The originating stream.
        /// </param>
        /// <param name="buffer">
        /// The data passed through the stream.
        /// </param>
        /// <param name="dropConnection">
        /// Whether or not to immediately terminate the stream.
        /// </param>
        private void OnWrappedStreamWrite(InspectionStream stream, Memory<byte> buffer, out bool dropConnection)
        {
            dropConnection = false;
            _streamInpsectionCb?.Invoke(stream.MessageInfo, StreamOperation.Write, buffer, out dropConnection);
        }
    }
}