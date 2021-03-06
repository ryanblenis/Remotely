﻿using Remotely.Server.Data;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using System.IO;
using System.Drawing;
using Remotely.Shared.Models;
using Remotely.Server.Models;

namespace Remotely.Server.Services
{
    public class RCDeviceSocketHub : Hub
    {
        public RCDeviceSocketHub(DataService dataService,
            IHubContext<BrowserSocketHub> browserHub,
            IHubContext<RCBrowserSocketHub> rcBrowserHub,
            IHubContext<DeviceSocketHub> deviceSocketHub,
            RemoteControlSessionRecorder rcSessionRecorder,
            ApplicationConfig appConfig)
        {
            DataService = dataService;
            BrowserHub = browserHub;
            RCBrowserHub = rcBrowserHub;
            DeviceHub = deviceSocketHub;
            RCSessionRecorder = rcSessionRecorder;
            AppConfig = appConfig;
        }

        public static ConcurrentDictionary<string, RCSessionInfo> SessionInfoList { get; } = new ConcurrentDictionary<string, RCSessionInfo>();
        public ApplicationConfig AppConfig { get; }
        public RemoteControlSessionRecorder RCSessionRecorder { get; }
        private IHubContext<BrowserSocketHub> BrowserHub { get; }
        private Size CurrentScreenSize
        {
            get
            {
                if (Context.Items.ContainsKey("CurrentScreenSize"))
                {
                    return (Size)Context.Items["CurrentScreenSize"];
                }
                else
                {
                    return Size.Empty;
                }
            }
            set
            {
                Context.Items["CurrentScreenSize"] = value;
            }
        }
        private DataService DataService { get; }

        private IHubContext<DeviceSocketHub> DeviceHub { get; }

        private IHubContext<RCBrowserSocketHub> RCBrowserHub { get; }

        private RCSessionInfo SessionInfo
        {
            get
            {
                if (Context.Items.ContainsKey("SessionInfo"))
                {
                    return (RCSessionInfo)Context.Items["SessionInfo"];
                }
                else
                {
                    return null;
                }
            }
            set
            {
                Context.Items["SessionInfo"] = value;
            }
        }
        private List<string> ViewerList
        {
            get
            {
                if (!Context.Items.ContainsKey("ViewerList"))
                {
                    Context.Items["ViewerList"] = new List<string>();
                }
                return Context.Items["ViewerList"] as List<string>;
            }
        }

        public Task CtrlAltDel()
        {
            return DeviceHub.Clients.Client(SessionInfo.ServiceID).SendAsync("CtrlAltDel");
        }
        public Task GetSessionID()
        {
            var random = new Random();
            var sessionID = "";
            for (var i = 0; i < 3; i++)
            {
                sessionID += random.Next(0, 999).ToString().PadLeft(3, '0');
            }
            Context.Items["SessionID"] = sessionID;

            SessionInfoList[Context.ConnectionId].AttendedSessionID = sessionID;

            return Clients.Caller.SendAsync("SessionID", sessionID);
        }

        public Task NotifyRequesterUnattendedReady(string browserHubConnectionID)
        {
            return BrowserHub.Clients.Client(browserHubConnectionID).SendAsync("UnattendedSessionReady", Context.ConnectionId);
        }

        public Task NotifyViewersRelaunchedScreenCasterReady(string[] viewerIDs)
        {
            return RCBrowserHub.Clients.Clients(viewerIDs).SendAsync("RelaunchedScreenCasterReady", Context.ConnectionId);
        }

        public override async Task OnConnectedAsync()
        {
            SessionInfo = new RCSessionInfo()
            {
                RCDeviceSocketID = Context.ConnectionId,
                StartTime = DateTime.Now
            };
            SessionInfoList.AddOrUpdate(Context.ConnectionId, SessionInfo, (id, si) => SessionInfo);

            await base.OnConnectedAsync();
        }
        public override async Task OnDisconnectedAsync(Exception exception)
        {
            SessionInfoList.Remove(Context.ConnectionId, out _);

            if (SessionInfo.Mode == Shared.Enums.RemoteControlMode.Normal)
            {
                await RCBrowserHub.Clients.Clients(ViewerList).SendAsync("ScreenCasterDisconnected");
            }
            else if (SessionInfo.Mode == Shared.Enums.RemoteControlMode.Unattended)
            {
                if (ViewerList.Count > 0)
                {
                    await RCBrowserHub.Clients.Clients(ViewerList).SendAsync("Reconnecting");
                    await DeviceHub.Clients.Client(SessionInfo.ServiceID).SendAsync("RestartScreenCaster", ViewerList, SessionInfo.ServiceID, Context.ConnectionId);
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        public void ReceiveDeviceInfo(string serviceID, string machineName, string deviceID)
        {
            SessionInfo.ServiceID = serviceID;
            SessionInfo.MachineName = machineName;
            SessionInfo.DeviceID = deviceID;
        }

        public Task SendAudioSample(byte[] buffer, List<string> viewerIDs)
        {
            return RCBrowserHub.Clients.Clients(viewerIDs).SendAsync("AudioSample", buffer);
        }

        public Task SendClipboardText(string clipboardText, List<string> viewerIDs)
        {
            return RCBrowserHub.Clients.Clients(viewerIDs).SendAsync("ClipboardTextChanged", clipboardText);
        }

        public Task SendConnectionFailedToViewers(List<string> viewerIDs)
        {
            return RCBrowserHub.Clients.Clients(viewerIDs).SendAsync("ConnectionFailed");
        }

        public Task SendCursorChange(CursorInfo cursor, List<string> viewerIDs)
        {
            return RCBrowserHub.Clients.Clients(viewerIDs).SendAsync("CursorChange", cursor);
        }

        public Task SendMachineName(string machineName, string viewerID)
        {
            return RCBrowserHub.Clients.Client(viewerID).SendAsync("ReceiveMachineName", machineName);
        }

        public Task SendRtcOfferToBrowser(string sdp, string viewerID)
        {
            if (AppConfig.UseWebRtc)
            {
                return RCBrowserHub.Clients.Client(viewerID).SendAsync("ReceiveRtcOffer", sdp);
            }

            return Task.CompletedTask;
        }
        public Task SendIceCandidateToBrowser(string candidate, int sdpMlineIndex, string sdpMid, string viewerID)
        {
            if (AppConfig.UseWebRtc)
            {
                return RCBrowserHub.Clients.Client(viewerID).SendAsync("ReceiveIceCandidate", candidate, sdpMlineIndex, sdpMid);
            }

            return Task.CompletedTask;
        }

        public Task SendScreenCapture(byte[] captureBytes, string rcBrowserHubConnectionID, int left, int top, int width, int height, DateTime captureTime)
        {
            if (AppConfig.RecordRemoteControlSessions)
            {
                RCSessionRecorder.SaveFrame(captureBytes, left, top, CurrentScreenSize.Width, CurrentScreenSize.Height, 
                                                rcBrowserHubConnectionID, SessionInfo.MachineName, SessionInfo.StartTime);
            }

            return RCBrowserHub.Clients.Client(rcBrowserHubConnectionID).SendAsync("ScreenCapture", captureBytes, left, top, width, height, captureTime);
        }

        public Task SendScreenCountToBrowser(int primaryScreenIndex, int screenCount, string rcBrowserHubConnectionID)
        {
            lock (ViewerList)
            {
                ViewerList.Add(rcBrowserHubConnectionID);
            }
            return RCBrowserHub.Clients.Client(rcBrowserHubConnectionID).SendAsync("ScreenCount", primaryScreenIndex, screenCount);
        }

        public Task SendScreenSize(int width, int height, string rcBrowserHubConnectionID)
        {
            CurrentScreenSize = new Size(width, height);
            return RCBrowserHub.Clients.Client(rcBrowserHubConnectionID).SendAsync("ScreenSize", width, height);
        }

        public Task SendViewerRemoved(string viewerID)
        {
            return RCBrowserHub.Clients.Clients(viewerID).SendAsync("ViewerRemoved");
        }


        public void ViewerDisconnected(string viewerID)
        {
            lock (ViewerList)
            {
                ViewerList.Remove(viewerID);
            }
        }
    }
}
