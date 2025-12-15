using UnityEngine;
using AtlasNet;
using AtlasNet.Rpc;

public class Door : NetBehaviour
{
	private int _openCount;

	void Start()
	{
		GetComponent<NetObject>().DebugSetIdentity(100, 1);
		ToggleServerRpc(3);
		ToggleServerRpc(2);
	}

	[ServerRpc]
	public void ToggleServerRpc(int amount)
	{
		if (IsServer)
		{
			ExecuteToggle(amount);
			return;
		}

		SendServerRpc(amount);
	}

	private void ExecuteToggle(int amount)
	{
		_openCount += amount;
		Debug.Log($"[AtlasNet] Door {NetObject.NetId} open count = {_openCount}");
	}
}
