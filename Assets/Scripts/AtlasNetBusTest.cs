using UnityEngine;
using AtlasNet;
using AtlasNet.Messaging;

public class AtlasNetBusTest : MonoBehaviour
{
	private void Awake()
	{
		AtlasNetManager.StartHost();

		AtlasNetManager.MessageBus.Subscribe<PingServerMessage>(
				AtlasTopics.Server,
				OnPingServer
		);

		AtlasNetManager.MessageBus.Publish(
				AtlasTopics.Server,
				new PingServerMessage
				{
					FromClientId = AtlasNetManager.LocalClientId
				}
		);
	}

	private void OnPingServer(PingServerMessage msg)
	{
		Debug.Log($"[AtlasNet] Server received ping from client {msg.FromClientId}");
	}
}
