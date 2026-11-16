import ctypes, socket, time, json, sys, unittest, threading
lib=ctypes.CDLL(sys.argv.pop(1))
class KeeperTests(unittest.TestCase):
    def setUp(self):
        with socket.socket() as s: s.bind(('127.0.0.1',0)); self.port=s.getsockname()[1]
        self.assertEqual(lib.mcpkeeper_start(self.port),0)
    def tearDown(self): lib.mcpkeeper_stop()
    def connect(self): return socket.create_connection(('127.0.0.1',self.port), timeout=2)
    def test_detached_reports_not_dispatched(self):
        with self.connect() as s:
            s.sendall(b'POST /mcp HTTP/1.1\r\nHost: localhost\r\nContent-Length: 2\r\n\r\n{}')
            data=b''
            while True:
                part=s.recv(4096)
                if not part: break
                data+=part
        self.assertIn(b'503 Service Unavailable',data)
        self.assertIn(b'Retry-After:',data)
        self.assertEqual(json.loads(data.split(b'\r\n\r\n')[1])['dispatch_state'],'not_started')
    def test_stale_handle_cannot_close_reused_slot(self):
        lib.mcpkeeper_attach()
        a=self.connect(); first=lib.mcpkeeper_dequeue(1000); lib.mcpkeeper_close_client(first); a.close()
        b=self.connect(); second=lib.mcpkeeper_dequeue(1000)
        try:
            self.assertNotEqual(first,second)
            lib.mcpkeeper_close_client(first)
            self.assertEqual(lib.mcpkeeper_is_client_connected(second),1)
        finally: b.close(); lib.mcpkeeper_close_client(second)
    def test_detach_does_not_wait_for_idle_headers(self):
        lib.mcpkeeper_attach(); s=self.connect(); time.sleep(.05)
        start=time.monotonic(); lib.mcpkeeper_detach(); elapsed=time.monotonic()-start
        s.close(); self.assertLess(elapsed,.3)
    def test_stop_restart_and_bind_failure_cleanup(self):
        lib.mcpkeeper_stop()
        with socket.socket() as occupied:
            occupied.bind(('127.0.0.1',self.port)); occupied.listen()
            self.assertNotEqual(lib.mcpkeeper_start(self.port),0)
        self.assertEqual(lib.mcpkeeper_start(self.port),0)
        lib.mcpkeeper_attach()
        with self.connect() as s:
            handle=lib.mcpkeeper_dequeue(1000); self.assertGreaterEqual(handle,0); lib.mcpkeeper_close_client(handle)
    def test_interrupt_read_releases_connection(self):
        lib.mcpkeeper_attach(); s=self.connect(); handle=lib.mcpkeeper_dequeue(1000)
        buffer=ctypes.create_string_buffer(16); result=[]
        thread=threading.Thread(target=lambda:result.append(lib.mcpkeeper_recv(handle,buffer,16)))
        thread.start(); time.sleep(.03); lib.mcpkeeper_close_client(handle); thread.join(1)
        s.close(); self.assertFalse(thread.is_alive()); self.assertLessEqual(result[0],0)
    def test_staged_response_is_flushed_exactly(self):
        lib.mcpkeeper_attach()
        with self.connect() as s:
            handle=lib.mcpkeeper_dequeue(1000)
            response=b'HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}'
            lib.mcpkeeper_stage_response(handle,response,len(response))
            lib.mcpkeeper_detach(); lib.mcpkeeper_flush_staged()
            self.assertEqual(s.recv(4096),response)
    def test_repeated_reload_cycles_and_old_handles(self):
        retired=[]
        for _ in range(50):
            lib.mcpkeeper_attach()
            with self.connect() as s:
                handle=lib.mcpkeeper_dequeue(1000)
                self.assertGreaterEqual(handle,0)
                for old in retired[-3:]: lib.mcpkeeper_close_client(old)
                self.assertEqual(lib.mcpkeeper_is_client_connected(handle),1)
                lib.mcpkeeper_close_client(handle); retired.append(handle)
            lib.mcpkeeper_detach()
        self.assertEqual(len(set(retired)),50)
unittest.main()
