"""Serve an existing Unity Web build alongside an AtlasNet editor/native server."""
import argparse
import functools
import http.server
import ipaddress
from pathlib import Path
import socket
import sys
from urllib.parse import parse_qsl, urlencode, urlsplit
import webbrowser


def find_build(project, explicit):
    if explicit:
        folder = Path(explicit).expanduser().resolve()
        if not (folder / "index.html").is_file():
            raise ValueError("The build folder must contain index.html: " + str(folder))
        return folder
    builds = project / "Builds"
    candidates = [p for p in builds.rglob("index.html")
                  if (p.parent / "Build").is_dir()
                  and "createUnityInstance" in p.read_text(encoding="utf-8", errors="replace")]
    if not candidates:
        raise ValueError("No Unity Web build found under " + str(builds)
                         + ". Build Web there first, or pass --build PATH.")
    return max(candidates, key=lambda p: p.stat().st_mtime).parent


def lan_addresses():
    try:
        addresses = {entry[4][0] for entry in socket.getaddrinfo(
            socket.gethostname(), None, socket.AF_INET, socket.SOCK_STREAM)}
    except OSError:
        return []
    return sorted(address for address in addresses
                  if not ipaddress.ip_address(address).is_loopback
                  and not ipaddress.ip_address(address).is_link_local
                  and not ipaddress.ip_address(address).is_unspecified)


class WebHandler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map,
                      ".wasm": "application/wasm", ".data": "application/octet-stream",
                      ".js": "application/javascript"}

    def guess_type(self, path):
        # Unity builds may serve precompressed files without a reverse proxy.
        for suffix in (".gz", ".br"):
            if str(path).endswith(suffix):
                path = str(path)[:-len(suffix)]
                break
        return super().guess_type(path)

    def end_headers(self):
        self.send_header("Cache-Control", "no-cache")
        path = Path(self.translate_path(urlsplit(self.path).path))
        if path.is_file():
            if path.suffix == ".gz":
                self.send_header("Content-Encoding", "gzip")
            elif path.suffix == ".br":
                self.send_header("Content-Encoding", "br")
        super().end_headers()

    def do_GET(self):
        request = urlsplit(self.path)
        query = dict(parse_qsl(request.query, keep_blank_values=True))
        if request.path in ("/", "/index.html") and not query.get("host"):
            # A device opening the bare HTTP address joins that same machine's
            # game server, rather than accidentally connecting to its own localhost.
            hostname = urlsplit("http://" + self.headers.get("Host", "127.0.0.1")).hostname
            query["host"] = self.server.game_host or hostname or "127.0.0.1"
            query.setdefault("port", str(self.server.game_port))
            self.send_response(307)
            self.send_header("Location", "/?" + urlencode(query))
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        super().do_GET()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", help="Explicit folder containing the Web build's index.html")
    parser.add_argument("--port", type=int, default=8094, help="HTTP serving port (default: 8094)")
    parser.add_argument("--game-port", type=int, default=7777, help="Unity NetworkManager port (default: 7777)")
    parser.add_argument("--game-host", help="Override the game server address if it runs on another PC")
    parser.add_argument("--bind", default="0.0.0.0", help="HTTP listen address (default: all IPv4 interfaces)")
    parser.add_argument("--open", action="store_true", help="Also open the localhost join link")
    args = parser.parse_args()
    if not (1 <= args.port <= 65535 and 1 <= args.game_port <= 65535):
        parser.error("Both ports must be between 1 and 65535.")
    try:
        project = Path(__file__).resolve().parent.parent
        build = find_build(project, args.build)
        handler = functools.partial(WebHandler, directory=str(build))
        with http.server.ThreadingHTTPServer((args.bind, args.port), handler) as server:
            server.daemon_threads = True
            server.game_port = args.game_port
            server.game_host = args.game_host

            def join_link(address):
                query = urlencode({"host": args.game_host or address, "port": args.game_port})
                return "http://{}:{}/?{}".format(address, args.port, query)

            local = join_link("127.0.0.1")
            print("\nAtlasNet Web server", flush=True)
            print("Build: " + str(build), flush=True)
            print("Local: " + local, flush=True)
            if args.bind == "0.0.0.0":
                for address in lan_addresses():
                    print("LAN:   " + join_link(address), flush=True)
            print("\nIn Unity: Play > Start Server Only, using game port {}.".format(args.game_port), flush=True)
            print("Additional Play Mode clients: UDP > Join as Client.", flush=True)
            print("This serves the existing Web build; it does not build or start Unity.", flush=True)
            print("For LAN, allow HTTP TCP {} and game TCP/UDP {} through Windows Firewall.".format(args.port, args.game_port), flush=True)
            print("Press Ctrl+C or close this window to stop.\n", flush=True)
            if args.open:
                webbrowser.open(local)
            try:
                server.serve_forever()
            except KeyboardInterrupt:
                print("\nWeb server stopped.", flush=True)
    except (OSError, ValueError) as error:
        print("ERROR: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
