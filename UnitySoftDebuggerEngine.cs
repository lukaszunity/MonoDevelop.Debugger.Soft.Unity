// 
// UnitySoftDebuggerEngine.cs
//   based on MoonlightSoftDebuggerEngine.cs
//  
// Author:
//       Michael Hutchinson <mhutchinson@novell.com>
//       Lucas Meijer <lucas@unity3d.com>
// 
// Copyright (c) 2009 Novell, Inc. (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;

using MonoDevelop.Debugger;
using MonoDevelop.Core;
using MonoDevelop.Core.Execution;
using Mono.Debugging.Soft;
using Mono.Debugging.Client;
using MonoDevelop.Debugger.Soft;

namespace MonoDevelop.Debugger.Soft.Unity
{
	public class UnitySoftDebuggerEngine: IDebuggerEngine
	{
		UnitySoftDebuggerSession session;
		static PlayerConnection unityPlayerConnection;
		
		internal static Dictionary<uint, PlayerConnection.PlayerInfo> UnityPlayers {
			get;
			private set;
		}

		internal static ConnectorRegistry ConnectorRegistry { get; private set; }


		static UnitySoftDebuggerEngine ()
		{
			UnityPlayers = new Dictionary<uint, PlayerConnection.PlayerInfo> ();
			ConnectorRegistry = new ConnectorRegistry();
			
			try {
			// HACK: Poll Unity players
			unityPlayerConnection = new PlayerConnection ();
			ThreadPool.QueueUserWorkItem (delegate {
				while (true) {
					lock (unityPlayerConnection) {
						unityPlayerConnection.Poll ();
					}
					Thread.Sleep (1000);
				}
			});
			} catch (Exception e)
			{
				MonoDevelop.Core.LoggingService.LogError ("Error launching player connection discovery service: Unity player discovery will be unavailable", e);
			}
		}
		
		public string Id {
			get {
				return "Mono.Debugger.Soft.Unity";
			}
		}
		
		static readonly List<string> UserAssemblies = new List<string>{
		};

		public bool CanDebugCommand (ExecutionCommand command)
		{
			return (command is UnityExecutionCommand && null == session);
		}
		
		public DebuggerStartInfo CreateDebuggerStartInfo (ExecutionCommand command)
		{
			var cmd = command as UnityExecutionCommand;
			if (null == cmd){ return null; }

			if (Util.UnityProcessId > 0)
			{
				int port = 56000 + ( Util.UnityProcessId % 1000);
				return new SoftDebuggerStartInfo(new SoftDebuggerConnectArgs("Unity", IPAddress.Loopback, port));
			}
			else
			{
				var msi = new UnityDebuggerStartInfo("Unity");
				// msi.SetUserAssemblies (null);
				msi.Arguments = string.Format("-projectPath \"{0}\"", cmd.ProjectPath);
				return msi;
			}
		}

		public DebuggerFeatures SupportedFeatures {
			get {
				return DebuggerFeatures.Breakpoints | 
					   DebuggerFeatures.Pause | 
					   DebuggerFeatures.Stepping | 
					   DebuggerFeatures.DebugFile |
					   DebuggerFeatures.ConditionalBreakpoints |
					   DebuggerFeatures.Tracepoints |
					   DebuggerFeatures.Catchpoints |
					   DebuggerFeatures.Attaching;
			}
		}
		
		public DebuggerSession CreateSession ()
		{
			session = new UnitySoftDebuggerSession (ConnectorRegistry);
			session.TargetExited += delegate{ session = null; };
			return session;
		}
		
		public ProcessInfo[] GetAttachableProcesses ()
		{
			int index = 1;
			List<ProcessInfo> processes = new List<ProcessInfo> ();
			Process[] systemProcesses = Process.GetProcesses ();
			StringComparison comparison = StringComparison.OrdinalIgnoreCase;
			
			if (null != unityPlayerConnection) {
				lock (unityPlayerConnection) {
					foreach (string player in unityPlayerConnection.AvailablePlayers) {
						try {
							PlayerConnection.PlayerInfo info = PlayerConnection.PlayerInfo.Parse (player);
							if (info.m_AllowDebugging) {
								UnityPlayers[info.m_Guid] = info;
								processes.Add (new ProcessInfo (info.m_Guid, info.m_Id));
								++index;
							}
						} catch {
							// Don't care; continue
						}
					}
				}
			}
			if (null != systemProcesses) {
				foreach (Process p in systemProcesses) {
					try {
						if ((p.ProcessName.StartsWith ("unity", comparison) ||
							p.ProcessName.Contains ("Unity.app")) &&
							!p.ProcessName.Contains ("UnityShader")) {
							processes.Add (new ProcessInfo (p.Id, string.Format ("{0} ({1})", "Unity Editor", p.ProcessName)));
						}
					} catch {
						// Don't care; continue
					}
				}
			}

			// Direct USB devices
			iOSDevices.GetUSBDevices(ConnectorRegistry, processes);

			return processes.ToArray ();
		}

		public string Name {
			get {
				return "Mono Soft Debugger for Unity";
			}
		}
	}
	
	class UnityDebuggerStartInfo : SoftDebuggerStartInfo
	{
		public UnityDebuggerStartInfo (string appName)
			: base (new SoftDebuggerConnectArgs (appName, IPAddress.Loopback, 57432))
		{
		}
	}


	// Allows to define how to setup and tear down connection for debugger to connect to the
	// debugee. For example to setup TCP tunneling over USB.
	public interface IUnityDbgConnector
	{
		SoftDebuggerStartInfo SetupConnection();
		void OnDisconnect();
	}


	// Manages a map from process id to IUnityDbgConnector, so that services can supply a list of
	// debugees and how to connect to them, and UnitySoftDebuggerSession can have a way for
	// establishing a connection to them.
	public class ConnectorRegistry
	{
		// This is used to map process id <-> unique string id. MonoDevelop attachment is built
		// around process ids.
		object processIdLock = new object();
		uint nextProcessId = 1000000;
		Dictionary<uint, string> processIdToUniqueId = new Dictionary<uint, string>();
		Dictionary<string, uint> uniqueIdToProcessId = new Dictionary<string, uint>();

		public Dictionary<uint, IUnityDbgConnector> Connectors { get; private set; }


		public uint GetProcessIdForUniqueId(string uid)
		{
			lock (processIdLock)
			{
				uint processId;
				if (uniqueIdToProcessId.TryGetValue(uid, out processId))
					return processId;

				processId = nextProcessId++;
				processIdToUniqueId.Add(processId, uid);
				uniqueIdToProcessId.Add(uid, processId);

				return processId;
			}
		}


		public string GetUniqueIdFromProcessId(uint processId)
		{
			lock (processIdLock) {
				string uid;
				if (processIdToUniqueId.TryGetValue(processId, out uid))
					return uid;

				return null;
			}
		}


		public ConnectorRegistry()
		{
			Connectors = new Dictionary<uint, IUnityDbgConnector>();
		}
	}
}
