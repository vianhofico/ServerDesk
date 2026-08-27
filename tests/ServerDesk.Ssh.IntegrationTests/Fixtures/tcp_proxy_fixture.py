#!/usr/bin/env python3
import argparse
import select
import socket
import socketserver
import struct


def read_exact(sock, count):
    data = bytearray()
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            raise ConnectionError("unexpected EOF")
        data.extend(chunk)
    return bytes(data)


def read_until_nul(sock, limit=4096):
    data = bytearray()
    while len(data) < limit:
        value = read_exact(sock, 1)
        if value == b"\x00":
            return bytes(data)
        data.extend(value)
    raise ValueError("NUL-terminated field exceeded limit")


def relay(left, right):
    sockets = [left, right]
    while True:
        readable, _, _ = select.select(sockets, [], [], 30)
        if not readable:
            continue
        for source in readable:
            destination = right if source is left else left
            data = source.recv(65536)
            if not data:
                return
            destination.sendall(data)


def open_target(host, port):
    return socket.create_connection((host, port), timeout=10)


class BaseProxyHandler(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            upstream = self.connect_upstream()
            try:
                relay(self.request, upstream)
            finally:
                upstream.close()
        except Exception:
            # CI integration tests surface the client-side failure. Keep fixture logs quiet and deterministic.
            return

    def connect_upstream(self):
        raise NotImplementedError


class HttpConnectHandler(BaseProxyHandler):
    def connect_upstream(self):
        data = bytearray()
        while b"\r\n\r\n" not in data:
            chunk = self.request.recv(4096)
            if not chunk:
                raise ConnectionError("HTTP proxy client disconnected")
            data.extend(chunk)
            if len(data) > 65536:
                raise ValueError("HTTP proxy request too large")

        first_line = bytes(data).split(b"\r\n", 1)[0].decode("ascii")
        method, authority, _ = first_line.split(" ", 2)
        if method.upper() != "CONNECT":
            self.request.sendall(b"HTTP/1.1 405 Method Not Allowed\r\n\r\n")
            raise ValueError("only CONNECT is supported")

        host, port_text = authority.rsplit(":", 1)
        upstream = open_target(host.strip("[]"), int(port_text))
        self.request.sendall(b"HTTP/1.1 200 Connection Established\r\nProxy-Agent: ServerDesk-CI\r\n\r\n")
        return upstream


class Socks4Handler(BaseProxyHandler):
    def connect_upstream(self):
        header = read_exact(self.request, 8)
        version, command, port = header[0], header[1], struct.unpack("!H", header[2:4])[0]
        address_bytes = header[4:8]
        _ = read_until_nul(self.request)
        if version != 4 or command != 1:
            self.request.sendall(b"\x00\x5b" + header[2:8])
            raise ValueError("unsupported SOCKS4 request")

        if address_bytes[:3] == b"\x00\x00\x00" and address_bytes[3] != 0:
            host = read_until_nul(self.request).decode("idna")
        else:
            host = socket.inet_ntoa(address_bytes)

        upstream = open_target(host, port)
        self.request.sendall(b"\x00\x5a" + header[2:8])
        return upstream


class Socks5Handler(BaseProxyHandler):
    def connect_upstream(self):
        version, method_count = read_exact(self.request, 2)
        methods = read_exact(self.request, method_count)
        if version != 5 or 0 not in methods:
            self.request.sendall(b"\x05\xff")
            raise ValueError("SOCKS5 no-auth method unavailable")
        self.request.sendall(b"\x05\x00")

        version, command, _, address_type = read_exact(self.request, 4)
        if version != 5 or command != 1:
            self.request.sendall(b"\x05\x07\x00\x01\x00\x00\x00\x00\x00\x00")
            raise ValueError("unsupported SOCKS5 request")

        if address_type == 1:
            host = socket.inet_ntoa(read_exact(self.request, 4))
        elif address_type == 3:
            length = read_exact(self.request, 1)[0]
            host = read_exact(self.request, length).decode("idna")
        elif address_type == 4:
            host = socket.inet_ntop(socket.AF_INET6, read_exact(self.request, 16))
        else:
            raise ValueError("unsupported SOCKS5 address type")

        port = struct.unpack("!H", read_exact(self.request, 2))[0]
        upstream = open_target(host, port)
        local_host, local_port = upstream.getsockname()[:2]
        try:
            packed = socket.inet_aton(local_host)
        except OSError:
            packed = b"\x00\x00\x00\x00"
        self.request.sendall(b"\x05\x00\x00\x01" + packed + struct.pack("!H", local_port))
        return upstream


class ThreadingProxyServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("http", "socks4", "socks5"), required=True)
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()

    handler = {
        "http": HttpConnectHandler,
        "socks4": Socks4Handler,
        "socks5": Socks5Handler,
    }[args.mode]
    with ThreadingProxyServer(("127.0.0.1", args.port), handler) as server:
        server.serve_forever()


if __name__ == "__main__":
    main()
