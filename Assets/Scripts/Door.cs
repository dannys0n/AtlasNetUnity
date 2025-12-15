using UnityEngine;
using AtlasNet;
using AtlasNet.Rpc;
using AtlasNet.Rpc.Messages;
using AtlasNet.Messaging;

public class Door : NetBehaviour
{
	private const ulong ToggleRpcId = 1;
	private bool _isOpen;
	void Start()
	{
		GetComponent<NetObject>().DebugSetIdentity(100, 1);
		ToggleServerRpc();
	}

	[ServerRpc]
	public void ToggleServerRpc()
	{
		if (IsServer)
		{
			ExecuteToggle();
			return;
		}

		AtlasNetManager.MessageBus.Publish(
				AtlasTopics.Server,
				new ServerRpcMessage
				{
					NetId = NetObject.NetId,
					RpcId = ToggleRpcId
				}
		);
	}

	public override void HandleServerRpc(ulong rpcId)
	{
		if (rpcId == ToggleRpcId)
		{
			ExecuteToggle();
		}
	}

	private void ExecuteToggle()
	{
		_isOpen = !_isOpen;
		Debug.Log($"[AtlasNet] Door {NetObject.NetId} toggled. Open = {_isOpen}");
	}
}
