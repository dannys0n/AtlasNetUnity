using UnityEngine;
using AtlasNet;
using AtlasNet.Rpc;
using AtlasNet.Rpc.Messages;
using AtlasNet.Messaging;

public class Door : NetBehaviour
{
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

		SendServerRpc();
	}

	private void ExecuteToggle()
	{
		_isOpen = !_isOpen;
		Debug.Log($"[AtlasNet] Door {NetObject.NetId} toggled. Open = {_isOpen}");
	}
}
