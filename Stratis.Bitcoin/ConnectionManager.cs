﻿using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using Stratis.Bitcoin.Logging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Stratis.Bitcoin.Configuration;
using System.Net;
using System.Text;

namespace Stratis.Bitcoin
{
	public class ConnectionManagerBehavior : NodeBehavior
	{
		public ConnectionManagerBehavior(bool inbound, ConnectionManager connectionManager)
		{
			Inbound = inbound;
			ConnectionManager = connectionManager;
		}

		public ConnectionManager ConnectionManager
		{
			get;
			private set;
		}
		public bool Inbound
		{
			get;
			private set;
		}
		public bool Whitelisted
		{
			get;
			internal set;
		}
		public bool OneTry
		{
			get;
			internal set;
		}

		public override object Clone()
		{
			return new ConnectionManagerBehavior(Inbound, ConnectionManager)
			{
				OneTry = OneTry,
				Whitelisted = Whitelisted,
			};
		}

		protected override void AttachCore()
		{
			this.AttachedNode.StateChanged += AttachedNode_StateChanged;
		}

		private void AttachedNode_StateChanged(Node node, NodeState oldState)
		{
			if(node.State == NodeState.HandShaked)
			{
				ConnectionManager.ConnectedNodes.Add(node);
				Logs.ConnectionManager.LogInformation("Node " + node.RemoteSocketAddress + " connected (" + (Inbound ? "inbound" : "outbound") + "), agent " + node.PeerVersion.UserAgent + ", height " + node.PeerVersion.StartHeight);
			}
			if(node.State == NodeState.Failed || node.State == NodeState.Offline)
			{
				Logs.ConnectionManager.LogInformation("Node " + node.RemoteSocketAddress + " offline");
				if(node.DisconnectReason != null && !String.IsNullOrEmpty(node.DisconnectReason.Reason))
					Logs.ConnectionManager.LogInformation("Reason: " + node.DisconnectReason.Reason);
				ConnectionManager.ConnectedNodes.Remove(node);
			}
		}

		protected override void DetachCore()
		{
			this.AttachedNode.StateChanged -= AttachedNode_StateChanged;
		}
	}

	public class ConnectionManager : IDisposable
	{
		public NodesGroup DiscoveredNodeGroup
		{
			get; set;
		}


		private readonly Network _Network;
		public Network Network
		{
			get
			{
				return _Network;
			}
		}

		public NodesGroup ConnectNodeGroup
		{
			get;
			private set;
		}
		public NodesGroup AddNodeNodeGroup
		{
			get;
			private set;
		}

		NodeConnectionParameters _Parameters;
		public ConnectionManager(Network network, NodeConnectionParameters parameters, ConnectionManagerArgs args)
		{
			_Network = network;
			_Parameters = parameters;
			parameters.UserAgent = "StratisBitcoin:" + GetVersion();

			if(args.Connect.Count == 0)
			{
				var cloneParameters = parameters.Clone();
				cloneParameters.TemplateBehaviors.Add(new ConnectionManagerBehavior(false, this));
				DiscoveredNodeGroup = CreateNodeGroup(cloneParameters);
				DiscoveredNodeGroup.CustomGroupSelector = WellKnownGroupSelectors.ByNetwork; //is the default, but I want to use it
				DiscoveredNodeGroup.Connect();
			}
			else
			{
				var cloneParameters = parameters.Clone();
				cloneParameters.TemplateBehaviors.Add(new ConnectionManagerBehavior(false, this));
				cloneParameters.TemplateBehaviors.Remove<AddressManagerBehavior>();
				var addrman = new AddressManager();
				addrman.Add(args.Connect.Select(c => new NetworkAddress(c)).ToArray(), IPAddress.Loopback);
				var addrmanBehavior = new AddressManagerBehavior(addrman);
				addrmanBehavior.Mode = AddressManagerBehaviorMode.None;
				cloneParameters.TemplateBehaviors.Add(addrmanBehavior);

				ConnectNodeGroup = CreateNodeGroup(cloneParameters);
				ConnectNodeGroup.MaximumNodeConnection = args.Connect.Count;
				ConnectNodeGroup.CustomGroupSelector = WellKnownGroupSelectors.ByEndpoint;
				ConnectNodeGroup.Connect();
			}

			{
				var cloneParameters = parameters.Clone();
				cloneParameters.TemplateBehaviors.Add(new ConnectionManagerBehavior(false, this));
				cloneParameters.TemplateBehaviors.Remove<AddressManagerBehavior>();
				var addrman = new AddressManager();
				addrman.Add(args.Connect.Select(c => new NetworkAddress(c)).ToArray(), IPAddress.Loopback);
				var addrmanBehavior = new AddressManagerBehavior(addrman);
				addrmanBehavior.Mode = AddressManagerBehaviorMode.AdvertizeDiscover;
				cloneParameters.TemplateBehaviors.Add(addrmanBehavior);

				AddNodeNodeGroup = CreateNodeGroup(cloneParameters);
				AddNodeNodeGroup.MaximumNodeConnection = args.AddNode.Count;
				AddNodeNodeGroup.CustomGroupSelector = WellKnownGroupSelectors.ByEndpoint;
				AddNodeNodeGroup.Connect();
			}

			StringBuilder logs = new StringBuilder();
			logs.AppendLine("Node listening on:");
			foreach(var listen in args.Listen)
			{
				var cloneParameters = parameters.Clone();
				var server = new NodeServer(Network);
				server.LocalEndpoint = listen.Endpoint;
				server.ExternalEndpoint = args.ExternalEndpoint;
				_Servers.Add(server);
				cloneParameters.TemplateBehaviors.Add(new ConnectionManagerBehavior(true, this)
				{
					Whitelisted = listen.Whitelisted
				});
				server.InboundNodeConnectionParameters = cloneParameters;
				server.Listen();
				logs.Append(listen.Endpoint.Address + ":" + listen.Endpoint.Port);
				if(listen.Whitelisted)
					logs.Append(" (whitelisted)");
				logs.AppendLine();
			}
			Logs.ConnectionManager.LogInformation(logs.ToString());
		}

		public string GetStats()
		{
			StringBuilder builder = new StringBuilder();
			lock(_Downloads)
			{
				PerformanceSnapshot diffTotal = new PerformanceSnapshot(0, 0);
				builder.AppendLine("====Connections====");
				foreach(var node in ConnectedNodes)
				{
					var newSnapshot = node.Counter.Snapshot();
					PerformanceSnapshot lastSnapshot = null;
					if(_Downloads.TryGetValue(node, out lastSnapshot))
					{

						var diff = newSnapshot - lastSnapshot;
						diffTotal = new PerformanceSnapshot(diff.TotalReadenBytes + diffTotal.TotalReadenBytes, diff.TotalWrittenBytes + diffTotal.TotalWrittenBytes) { Start = diff.Start, Taken = diff.Taken  };
						builder.AppendLine(node.RemoteSocketAddress + ":" + node.RemoteSocketPort + "\t => R: " + ToKBSec(lastSnapshot.ReadenBytesPerSecond) + "\tW: " + ToKBSec(lastSnapshot.WrittenBytesPerSecond));
					}
					_Downloads.AddOrReplace(node, newSnapshot);
				}
				builder.AppendLine("==========================");
				builder.AppendLine("Total\t => R: " + ToKBSec(diffTotal.ReadenBytesPerSecond) + "\tW: " + ToKBSec(diffTotal.WrittenBytesPerSecond));
				builder.AppendLine("==========================");

				//TODO: Hack, we should just clean nodes that are not connect anymore
				if(_Downloads.Count > 1000)
					_Downloads.Clear();
			}
			return builder.ToString();
		}

		private string ToKBSec(ulong bytesPerSec)
		{
			double speed = ((double)bytesPerSec / 1024.0);
			return speed.ToString("0.00") + " KB/S";
		}

		Dictionary<Node, PerformanceSnapshot> _Downloads = new Dictionary<Node, PerformanceSnapshot>();

		List<NodeServer> _Servers = new List<NodeServer>();

		private NodesGroup CreateNodeGroup(NodeConnectionParameters cloneParameters)
		{
			return new NodesGroup(Network, cloneParameters, new NodeRequirement()
			{
				MinVersion = ProtocolVersion.SENDHEADERS_VERSION,
				RequiredServices = NodeServices.Network,
			});
		}

		private string GetVersion()
		{
			var match = Regex.Match(this.GetType().AssemblyQualifiedName, "Version=([0-9]+\\.[0-9]+\\.[0-9]+)\\.");
			return match.Groups[1].Value;
		}

		public void Dispose()
		{
			if(DiscoveredNodeGroup != null)
				DiscoveredNodeGroup.Dispose();
			if(ConnectNodeGroup != null)
				ConnectNodeGroup.Dispose();
			if(AddNodeNodeGroup != null)
				AddNodeNodeGroup.Dispose();
			foreach(var server in _Servers)
				server.Dispose();
			foreach(var node in ConnectedNodes.Where(n => n.Behaviors.Find<ConnectionManagerBehavior>().OneTry))
				node.Disconnect();
		}


		private readonly NodesCollection _ConnectedNodes = new NodesCollection();
		public NodesCollection ConnectedNodes
		{
			get
			{
				return _ConnectedNodes;
			}
		}

		public void AddNode(IPEndPoint endpoint)
		{
			var addrman = AddressManagerBehavior.GetAddrman(AddNodeNodeGroup.NodeConnectionParameters);
			addrman.Add(new NetworkAddress(endpoint));
			AddNodeNodeGroup.MaximumNodeConnection++;
		}

		public void RemoveNode(IPEndPoint endpoint)
		{
			//TODO
			throw new NotSupportedException();
		}

		public Node Connect(IPEndPoint endpoint)
		{
			var cloneParameters = _Parameters.Clone();
			cloneParameters.TemplateBehaviors.Add(new ConnectionManagerBehavior(false, this)
			{
				OneTry = true
			});
			var node = Node.Connect(Network, endpoint, cloneParameters);
			node.VersionHandshake();
			return node;
		}
	}
}