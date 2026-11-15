mergeInto(LibraryManager.library, {
    $AtlasNetWebSocket: { connection: null },

    AtlasNetWebSocketOpen__deps: ['$AtlasNetWebSocket'],
    AtlasNetWebSocketOpen: function (url) {
        if (AtlasNetWebSocket.connection) return -1;
        try {
            var socket = new WebSocket(UTF8ToString(url), ['binary']);
            var connection = { socket: socket, status: 0, chunks: [], head: 0, offset: 0, pending: 0 };
            AtlasNetWebSocket.connection = connection;
            socket.binaryType = 'arraybuffer';
            socket.onopen = function () { connection.status = 1; };
            socket.onclose = socket.onerror = function () { connection.status = -1; };
            socket.onmessage = function (event) {
                if (!(event.data instanceof ArrayBuffer) ||
                    connection.pending + event.data.byteLength > 32 * 1024 * 1024) {
                    connection.status = -1;
                    socket.close(1000);
                    return;
                }
                if (event.data.byteLength === 0) return;
                connection.chunks.push(new Uint8Array(event.data));
                connection.pending += event.data.byteLength;
            };
            return 1;
        } catch (error) {
            return -1;
        }
    },

    AtlasNetWebSocketClose__deps: ['$AtlasNetWebSocket'],
    AtlasNetWebSocketClose: function () {
        var connection = AtlasNetWebSocket.connection;
        AtlasNetWebSocket.connection = null;
        if (connection) connection.socket.close();
    },

    AtlasNetWebSocketStatus__deps: ['$AtlasNetWebSocket'],
    AtlasNetWebSocketStatus: function () {
        var connection = AtlasNetWebSocket.connection;
        return connection ? connection.status : -1;
    },

    AtlasNetWebSocketRead__deps: ['$AtlasNetWebSocket'],
    AtlasNetWebSocketRead: function (buffer, offset, count) {
        var connection = AtlasNetWebSocket.connection;
        var copied = 0;
        while (copied < count && connection.pending > 0) {
            var chunk = connection.chunks[connection.head];
            var size = Math.min(count - copied, chunk.length - connection.offset);
            HEAPU8.set(chunk.subarray(connection.offset, connection.offset + size), buffer + offset + copied);
            copied += size;
            connection.offset += size;
            connection.pending -= size;
            if (connection.offset === chunk.length) {
                connection.chunks[connection.head++] = null;
                connection.offset = 0;
            }
        }
        if (connection.head > 64 || (connection.head > 0 && connection.pending === 0)) {
            connection.chunks = connection.chunks.slice(connection.head);
            connection.head = 0;
        }
        return copied;
    },

    AtlasNetWebSocketSend__deps: ['$AtlasNetWebSocket'],
    AtlasNetWebSocketSend: function (buffer, count) {
        var connection = AtlasNetWebSocket.connection;
        if (!connection || connection.socket.readyState !== WebSocket.OPEN) return -1;
        if (connection.socket.bufferedAmount >= 1024 * 1024) return 0;
        try {
            connection.socket.send(HEAPU8.subarray(buffer, buffer + count));
            return 1;
        } catch (error) {
            return -1;
        }
    }
});
