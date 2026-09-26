use std::{
    io::{self, BufRead, BufReader, Write},
    net::{Ipv4Addr, SocketAddr, TcpListener, TcpStream},
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant},
};

use serde_json::{json, Value};

struct TestServer {
    child: Child,
    address: SocketAddr,
}

impl TestServer {
    fn start() -> Self {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let address = listener.local_addr().unwrap();
        drop(listener);
        let child = Command::new(env!("CARGO_BIN_EXE_ossm-sim-bridge"))
            .arg("--listen")
            .arg(address.to_string())
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        Self { child, address }
    }

    fn connect_owner(&mut self) -> (BufReader<TcpStream>, Value) {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            if let Ok(stream) = TcpStream::connect(self.address) {
                stream
                    .set_read_timeout(Some(Duration::from_millis(100)))
                    .unwrap();
                let mut client = BufReader::new(stream);
                if send(&mut client, json!({"v":1,"id":1,"op":"status"})).is_ok() {
                    if let Ok(reply) = read_reply(&mut client, 1, deadline) {
                        return (client, reply);
                    }
                }
            }
            assert!(
                self.child.try_wait().unwrap().is_none(),
                "test bridge exited early"
            );
            assert!(
                Instant::now() < deadline,
                "test bridge did not admit a client"
            );
            thread::yield_now();
        }
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn send(client: &mut BufReader<TcpStream>, request: Value) -> io::Result<()> {
    client
        .get_mut()
        .write_all(&serde_json::to_vec(&request).unwrap())?;
    client.get_mut().write_all(b"\n")
}

fn read_reply(client: &mut BufReader<TcpStream>, id: u64, deadline: Instant) -> io::Result<Value> {
    loop {
        let mut line = String::new();
        match client.read_line(&mut line) {
            Ok(0) => return Err(io::Error::from(io::ErrorKind::UnexpectedEof)),
            Ok(_) => {
                let reply: Value = serde_json::from_str(&line).map_err(io::Error::other)?;
                if reply.get("id") == Some(&json!(id)) {
                    return Ok(reply);
                }
            }
            Err(error)
                if matches!(
                    error.kind(),
                    io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock
                ) => {}
            Err(error) => return Err(error),
        }
        if Instant::now() >= deadline {
            return Err(io::Error::from(io::ErrorKind::TimedOut));
        }
    }
}

fn assert_stopped(state: &Value) {
    assert_eq!(state["desired"]["speed"], 0);
    assert_eq!(state["enabled"], false);
    assert!(state["commandId"].is_null());
    assert_eq!(state["connected"], true);
}

#[test]
fn disconnected_client_pending_write_cannot_move_its_tcp_successor() {
    let mut server = TestServer::start();
    let (mut first, initial) = server.connect_owner();
    assert_stopped(&initial["state"]);

    let delay = Duration::from_millis(1500);
    send(
        &mut first,
        json!({"v":1,"id":2,"op":"delay","value":delay.as_millis()}),
    )
    .unwrap();
    assert_eq!(
        read_reply(&mut first, 2, Instant::now() + Duration::from_secs(2)).unwrap()["ok"],
        true
    );
    send(
        &mut first,
        json!({"v":1,"id":3,"op":"write","commandId":30,
        "desired":{"pattern":0,"speed":30,"stroke":10,"depth":20,"sensation":50}}),
    )
    .unwrap();
    send(&mut first, json!({"v":1,"id":4,"op":"status"})).unwrap();
    let queued = read_reply(&mut first, 4, Instant::now() + Duration::from_secs(2)).unwrap();
    assert_stopped(&queued["state"]);
    let accepted_by = Instant::now();
    drop(first);

    let (mut second, connected) = server.connect_owner();
    assert_stopped(&connected["state"]);
    thread::sleep(
        (accepted_by + delay + Duration::from_millis(100))
            .saturating_duration_since(Instant::now()),
    );
    send(&mut second, json!({"v":1,"id":5,"op":"status"})).unwrap();
    let after_deadline =
        read_reply(&mut second, 5, Instant::now() + Duration::from_secs(2)).unwrap();
    assert_stopped(&after_deadline["state"]);
    assert_eq!(
        after_deadline["state"]["changeRevision"],
        connected["state"]["changeRevision"]
    );
}
