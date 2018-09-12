/*
* Copyright © 2017 Cloudveil Technology Inc.
* This Source Code Form is subject to the terms of the Mozilla Public
* License, v. 2.0. If a copy of the MPL was not distributed with this
* file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

﻿using System;
using System.Collections.Generic;
using System.Text;

namespace CitadelCore.Diagnostics
{
    /// <summary>
    /// Delegate for CitadelCore to report a completed web session to the filtering service.
    /// </summary>
    /// <param name="webSession">Information about the web session</param>
    public delegate void SessionReportedHandler(DiagnosticsWebSession webSession);

    /// <summary>
    /// CitadelCore.Net.FilterHttpResponseHandler and CitadelCore.Net.FilterWebsocketHandler will use this class to report their
    /// diagnostics data.
    /// </summary>
    public static class Collector
    {
        /// <summary>
        /// If true, the diagnostics collector calls OnSessionReported
        /// </summary>
        public static bool IsDiagnosticsEnabled { get; set; }

        /// <summary>
        /// Event called when CitadelCore completes a web session.
        /// </summary>
        public static event SessionReportedHandler OnSessionReported;

        /// <summary>
        /// Report a web session to the collector.
        /// </summary>
        /// <param name="webSession"></param>
        public static void ReportSession(DiagnosticsWebSession webSession)
        {
            if (IsDiagnosticsEnabled)
            {
                OnSessionReported?.Invoke(webSession);
            }
        }

    }

    /// <summary>
    /// This is the main diagnostics class that the handlers will fill out.
    /// </summary>
    public class DiagnosticsWebSession
    {
        /// <summary>
        /// The date the web session started.
        /// </summary>
        public DateTime DateStarted { get; set; }

        /// <summary>
        /// The date the web session ended.
        /// </summary>
        public DateTime DateEnded { get; set; }

        /// <summary>
        /// The request body as sent by the client to the proxy.
        /// </summary>
        public byte[] ClientRequestBody { get; set; }

        /// <summary>
        /// The request body as sent by the proxy to the server.
        /// 
        /// Is currently identical to ClientRequestBody <see cref="ClientRequestBody"/>
        /// </summary>
        public byte[] ServerRequestBody { get; set; }

        /// <summary>
        /// The request headers as sent by the client to the proxy.
        /// </summary>
        public string ClientRequestHeaders { get; set; }

        /// <summary>
        /// The request headers as sent by the proxy to the server.
        /// </summary>
        public string ServerRequestHeaders { get; set; }

        /// <summary>
        /// The status code of the request as returned from the server.
        /// </summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// The URI requested by the client.
        /// </summary>
        public string ClientRequestUri { get; set; }

        /// <summary>
        /// The URI requested from the server by the proxy.
        /// 
        /// Usually the same as <see cref="ClientRequestUri"/>
        /// </summary>
        public string ServerRequestUri { get; set; }

        /// <summary>
        /// The response headers as returned from the server.
        /// </summary>
        public string ServerResponseHeaders { get; set; }

        /// <summary>
        /// The response body as returned from the server.
        /// </summary>
        public byte[] ServerResponseBody { get; set; }
    }
}