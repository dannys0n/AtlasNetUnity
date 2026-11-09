using System.Text;
using AtlasNet;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Collections.Generic;

namespace AtlasNet.M2
{
    public sealed class NetworkMetricsPanel : MonoBehaviour
    {
        [SerializeField] NetworkManager manager;
        [SerializeField] GameObject panel;
        [SerializeField] TMP_Text metrics;

        readonly StringBuilder content = new(512);
        readonly StringBuilder clientContent = new(512);
        Dictionary<SessionId, long> previousClients = new();
        Dictionary<SessionId, long> currentClients = new();
        TMP_Text clientRows;
        ScrollRect clientScroll;

        float sampleTime;
        long lastBytes;

        void Awake()
        {
            if (!manager) manager = GetComponentInParent<NetworkManager>();
            BuildClientTable();
            panel.SetActive(false);
        }

        void Update()
        {
            if (!manager) manager = FindAnyObjectByType<NetworkManager>();
            if (!manager || !manager.IsRunning)
            {
                if (panel.activeSelf) panel.SetActive(false);
                return;
            }

            float now = Time.unscaledTime;
            var selected = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            bool editing = selected && selected.TryGetComponent<TMP_InputField>(out var field) && field.isFocused;
            if (!editing && Input.GetKeyDown(KeyCode.Tab))
            {
                panel.SetActive(!panel.activeSelf);
                if (panel.activeSelf)
                {
                    sampleTime = now;
                    lastBytes = manager.BytesSent;
                    previousClients.Clear();
                    Refresh(0, 0);
                }
            }

            float elapsed = now - sampleTime;
            if (!panel.activeSelf || elapsed < .5f) return;
            long bytes = manager.BytesSent;
            Refresh(System.Math.Max(0, bytes - lastBytes) / elapsed / 1024.0, elapsed);
            lastBytes = bytes;
            sampleTime = now;
        }

        void Refresh(double upload, float elapsed)
        {
            content.Clear();
            string role = manager.IsWorker ? "Worker" : manager.IsHost ? "Host" : manager.IsServer ? "Server" : "Client";
            content.Append("Instance<pos=48%>").AppendLine(role);
            content.Append("Sessions<pos=48%>").AppendLine(manager.ConnectedClientsIds.Count.ToString());
            content.Append("Local objects<pos=48%>").AppendLine(manager.SpawnedCount.ToString());
            if (manager.IsServer)
            {
                content.Append("Workers<pos=48%>").AppendLine(manager.WorkerCount.ToString());
                content.Append("Authority<pos=48%>").AppendLine(manager.LocalAuthorityCount.ToString());
                content.Append("Handoffs<pos=48%>").AppendLine(manager.PendingHandoffCount.ToString());
            }
            content.Append("Network tick<pos=48%>").Append(manager.Tick).Append(" / ").Append(manager.TickRate).AppendLine(" Hz");
            content.Append("Tick CPU<pos=48%>").Append(manager.LastTickMilliseconds.ToString("F2")).AppendLine(" ms");
            content.Append("Sent<pos=48%>").Append(upload.ToString("F1")).AppendLine(" KiB/s");
            content.Append("Total sent<pos=48%>").Append((manager.BytesSent / 1024.0).ToString("F1")).Append(" KiB");
            metrics.SetText(content);
            RefreshClients(elapsed);
        }

        void BuildClientTable()
        {
            var frame = (RectTransform)panel.transform;
            frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 960);
            var overview = metrics.rectTransform;
            overview.anchorMax = new Vector2(.5f, overview.anchorMax.y);

            var viewport = new GameObject("Client metrics viewport", typeof(RectTransform),
                typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            var rect = (RectTransform)viewport.transform;
            rect.SetParent(frame, false);
            rect.anchorMin = new Vector2(.5f, 0);
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(0, 54);
            rect.offsetMax = new Vector2(-28, -112);
            viewport.GetComponent<Image>().color = Color.clear;

            clientRows = Instantiate(metrics, rect);
            clientRows.name = "Client metrics rows";
            clientRows.raycastTarget = false;
            var rows = clientRows.rectTransform;
            rows.anchorMin = new Vector2(0, 1);
            rows.anchorMax = Vector2.one;
            rows.pivot = new Vector2(.5f, 1);
            rows.anchoredPosition = Vector2.zero;
            rows.sizeDelta = Vector2.zero;

            clientScroll = viewport.GetComponent<ScrollRect>();
            clientScroll.viewport = rect;
            clientScroll.content = rows;
            clientScroll.horizontal = false;
            clientScroll.movementType = ScrollRect.MovementType.Clamped;
            clientScroll.scrollSensitivity = 32;

            var header = Instantiate(clientRows, frame);
            header.name = "Client metrics headings";
            var heading = header.rectTransform;
            heading.anchorMin = new Vector2(.5f, 1);
            heading.anchorMax = Vector2.one;
            heading.anchoredPosition = new Vector2(-14, -78);
            heading.sizeDelta = new Vector2(-28, 34);
            header.text = "Peer<pos=45%>KiB/s<pos=75%>Ping";
        }

        void RefreshClients(float elapsed)
        {
            clientContent.Clear();
            currentClients.Clear();
            if (manager.IsServer)
            {
                foreach (var session in manager.ConnectedClientsIds)
                    AppendClient(session, session.ToString(), elapsed);
            }
            else AppendClient(default, "Host", elapsed);

            if (clientContent.Length == 0) clientContent.Append("No connected clients");
            clientRows.SetText(clientContent);
            clientRows.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Max(clientScroll.viewport.rect.height, clientRows.preferredHeight + 4));
            (previousClients, currentClients) = (currentClients, previousClients);
        }

        void AppendClient(SessionId session, string label, float elapsed)
        {
            bool available = manager.TryGetPeerMetrics(session, out long sent, out float ping);
            bool sampled = available && elapsed > 0 && previousClients.TryGetValue(session, out _);
            clientContent.Append(label).Append("<pos=45%>")
                .Append(sampled ? (System.Math.Max(0, sent - previousClients[session]) / elapsed / 1024.0).ToString("F1") : "—")
                .Append("<pos=75%>").AppendLine(ping >= 0 ? ping.ToString("F0") + " ms" : "—");
            if (available) currentClients[session] = sent;
        }
    }
}
