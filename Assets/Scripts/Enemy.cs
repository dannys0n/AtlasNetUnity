using AtlasNet;
using AtlasNet.Rpc;
using UnityEngine;

public class Enemy : NetBehaviour
{
	private int _health = 100;

	private void Start()
	{
		DamagePayload payload = new DamagePayload
		{
			Amount = 25,
			Source = 42
		};

		DamageServerRpc(payload);
	}

	[ServerRpc]
	public void DamageServerRpc(DamagePayload payload)
	{
		if (IsServer)
		{
			_health -= payload.Amount;
			Debug.Log($"Enemy took {payload.Amount} from {payload.Source}. Health = {_health}");
			return;
		}

		SendServerRpc(new object[] { payload });
	}
}
