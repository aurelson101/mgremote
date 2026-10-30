"""Offline picocom/PTY regression. No switch, physical port or saved data is used."""
import json
import os
import pty
import select
import subprocess
import sys
import time


def receive(fd, needle, timeout=8):
    output = b""
    deadline = time.monotonic() + timeout
    while needle not in output and time.monotonic() < deadline:
        ready, _, _ = select.select([fd], [], [], 0.2)
        if ready:
            output += os.read(fd, 4096)
    if needle not in output:
        raise RuntimeError("Serial PTY did not receive expected data")


device, device_slave = pty.openpty()
front, front_slave = pty.openpty()
process = None
try:
    settings = os.environ.copy()
    settings["APPIMAGE_EXTRACT_AND_RUN"] = "1"
    args = json.loads(subprocess.check_output(
        [sys.argv[1], "--serial-plan=" + os.ttyname(device_slave)], env=settings, text=True, timeout=30))
    process = subprocess.Popen(["/usr/bin/picocom", *args], stdin=front_slave,
                               stdout=front_slave, stderr=front_slave, start_new_session=True)
    receive(front, b"Terminal ready")
    os.write(device, b"switch#")
    receive(front, b"switch#")
    os.write(front, b"show version\r")
    receive(device, b"show version\r")
    os.write(front, b"\x01\x18")  # picocom normal exit: Ctrl+A, Ctrl+X
    if process.wait(timeout=8) != 0:
        raise RuntimeError("Serial terminal did not exit cleanly")
    print("PASS: packaged serial arguments, picocom PTY bidirectional I/O and normal shutdown; no physical hardware.")
finally:
    if process is not None and process.poll() is None:
        process.kill()
        process.wait(timeout=5)
    for fd in (device, device_slave, front, front_slave):
        os.close(fd)
