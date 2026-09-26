use std::{
    io::{self, Read, Write},
    net::{Shutdown, TcpListener, TcpStream},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender, TrySendError},
        Arc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

use ossm_sim_bridge::{
    framing::{Frame, Framer},
    protocol::MAX_FRAME_BYTES,
};
use serde::Serialize;

pub const INPUT_CAPACITY: usize = 8;
const OUTPUT_CAPACITY: usize = 32;
const CLIENT_WRITE_TIMEOUT: Duration = Duration::from_secs(1);
const CLIENT_READ_TIMEOUT: Duration = Duration::from_millis(250);
const CLIENT_CLEANUP_TIMEOUT: Duration = Duration::from_secs(2);

pub enum Incoming {
    Frame(Frame),
    Eof,
    Failed,
}

pub fn accept_client(
    listener: &TcpListener,
    occupied: bool,
) -> Result<Option<TcpStream>, &'static str> {
    match listener.accept() {
        Ok((stream, _)) if occupied => {
            let _ = stream.shutdown(Shutdown::Both);
            Ok(None)
        }
        Ok((stream, _)) => {
            if stream.set_nonblocking(false).is_err() {
                return Ok(None);
            }
            Ok(Some(stream))
        }
        Err(error)
            if matches!(
                error.kind(),
                io::ErrorKind::WouldBlock
                    | io::ErrorKind::Interrupted
                    | io::ErrorKind::ConnectionAborted
                    | io::ErrorKind::ConnectionReset
            ) =>
        {
            Ok(None)
        }
        Err(_) => Err("listener_failed"),
    }
}

pub struct Transport {
    pub incoming: Receiver<Incoming>,
    outgoing: SyncSender<Vec<u8>>,
    stream: TcpStream,
    lost: Arc<AtomicBool>,
    reader: JoinHandle<()>,
    writer: JoinHandle<Result<(), &'static str>>,
}

impl Transport {
    pub fn start(stream: TcpStream) -> Result<Self, &'static str> {
        stream
            .set_read_timeout(Some(CLIENT_READ_TIMEOUT))
            .map_err(|_| "client_failed")?;
        stream
            .set_write_timeout(Some(CLIENT_WRITE_TIMEOUT))
            .map_err(|_| "client_failed")?;
        let read_stream = stream.try_clone().map_err(|_| "client_failed")?;
        let mut write_stream = stream.try_clone().map_err(|_| "client_failed")?;
        let (input_tx, incoming) = mpsc::sync_channel(INPUT_CAPACITY);
        let (outgoing, output_rx) = mpsc::sync_channel::<Vec<u8>>(OUTPUT_CAPACITY);
        let lost = Arc::new(AtomicBool::new(false));
        let reader_lost = lost.clone();
        let reader = thread::Builder::new()
            .name("bridge-client-reader".into())
            .spawn(move || read_input(read_stream, input_tx, reader_lost))
            .map_err(|_| "client_thread_failed")?;
        let writer_lost = lost.clone();
        let writer = match thread::Builder::new()
            .name("bridge-client-writer".into())
            .spawn(move || {
                for frame in output_rx {
                    if writer_lost.load(Ordering::Acquire) {
                        return Ok(());
                    }
                    if write_stream
                        .write_all(&frame)
                        .and_then(|_| write_stream.flush())
                        .is_err()
                    {
                        writer_lost.store(true, Ordering::Release);
                        return Err("client_write_failed");
                    }
                }
                Ok(())
            }) {
            Ok(writer) => writer,
            Err(_) => {
                let _ = stream.shutdown(Shutdown::Both);
                drop(incoming);
                let _ = reader.join();
                return Err("client_thread_failed");
            }
        };
        Ok(Self {
            incoming,
            outgoing,
            stream,
            lost,
            reader,
            writer,
        })
    }

    pub fn lost(&self) -> bool {
        self.lost.load(Ordering::Acquire)
    }

    pub fn send(&self, message: &impl Serialize) -> Result<(), &'static str> {
        let mut bytes = serde_json::to_vec(message).map_err(|_| "serialization_failed")?;
        if bytes.len() > MAX_FRAME_BYTES {
            return Err("output_too_large");
        }
        bytes.push(b'\n');
        // Do not stall the native engine while waiting for a non-reading client.
        match self.outgoing.try_send(bytes) {
            Ok(()) => Ok(()),
            Err(TrySendError::Full(_)) => Err("client_backpressure"),
            Err(TrySendError::Disconnected(_)) => Err("client_write_failed"),
        }
    }

    pub fn check_writer(&self) -> Result<(), &'static str> {
        if self.writer.is_finished() {
            Err("client_write_failed")
        } else {
            Ok(())
        }
    }

    pub fn finish(self) -> Result<(), &'static str> {
        self.lost.store(true, Ordering::Release);
        let _ = self.stream.shutdown(Shutdown::Both);
        drop(self.incoming);
        drop(self.outgoing);
        let deadline = Instant::now() + CLIENT_CLEANUP_TIMEOUT;
        while !self.writer.is_finished() || !self.reader.is_finished() {
            if Instant::now() >= deadline {
                return Err("client_cleanup_timeout");
            }
            thread::sleep(Duration::from_millis(5));
        }
        self.reader.join().map_err(|_| "client_thread_failed")?;
        self.writer.join().map_err(|_| "client_thread_failed")?
    }
}

fn read_input(mut input: impl Read, tx: SyncSender<Incoming>, lost: Arc<AtomicBool>) {
    let mut framer = Framer::default();
    let mut chunk = [0; 512];
    loop {
        if lost.load(Ordering::Acquire) {
            return;
        }
        match input.read(&mut chunk) {
            Ok(0) => {
                lost.store(true, Ordering::Release);
                let _ = tx.send(Incoming::Eof);
                return;
            }
            Ok(count) => {
                for &byte in &chunk[..count] {
                    if let Some(frame) = framer.push(byte) {
                        if tx.try_send(Incoming::Frame(frame)).is_err() {
                            lost.store(true, Ordering::Release);
                            return;
                        }
                    }
                }
            }
            Err(error)
                if matches!(
                    error.kind(),
                    io::ErrorKind::Interrupted
                        | io::ErrorKind::WouldBlock
                        | io::ErrorKind::TimedOut
                ) => {}
            Err(_) => {
                lost.store(true, Ordering::Release);
                let _ = tx.send(Incoming::Failed);
                return;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::{Ipv4Addr, TcpListener, TcpStream};

    #[test]
    fn panicked_client_writer_reports_thread_failure_on_join() {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let client = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let stream = listener.accept().unwrap().0;
        let (_input_tx, incoming) = mpsc::sync_channel(INPUT_CAPACITY);
        let (outgoing, _output_rx) = mpsc::sync_channel(OUTPUT_CAPACITY);
        let reader = thread::spawn(|| {});
        let writer =
            thread::spawn(|| -> Result<(), &'static str> { panic!("test-owned writer failure") });
        let transport = Transport {
            incoming,
            outgoing,
            stream,
            lost: Arc::new(AtomicBool::new(false)),
            reader,
            writer,
        };

        assert_eq!(
            crate::client_cleanup_result(transport.finish()),
            Err("client_thread_failed")
        );
        drop(client);
    }

    #[test]
    fn second_client_is_refused_without_replacing_the_first() {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let address = listener.local_addr().unwrap();
        let mut first = TcpStream::connect(address).unwrap();
        let mut owner = accept_client(&listener, false).unwrap().unwrap();

        let mut second = TcpStream::connect(address).unwrap();
        second
            .set_read_timeout(Some(Duration::from_millis(500)))
            .unwrap();
        assert!(accept_client(&listener, true).unwrap().is_none());
        let mut byte = [0];
        match second.read(&mut byte) {
            Ok(0) => {}
            Err(error) if error.kind() == io::ErrorKind::ConnectionReset => {}
            other => panic!("second client was not promptly closed: {other:?}"),
        }

        first.write_all(b"x").unwrap();
        owner
            .set_read_timeout(Some(Duration::from_millis(500)))
            .unwrap();
        assert_eq!(owner.read(&mut byte).unwrap(), 1);
        assert_eq!(byte, *b"x");

        drop(owner);
        drop(first);
        let next = TcpStream::connect(address).unwrap();
        assert!(accept_client(&listener, false).unwrap().is_some());
        drop(next);
    }

    #[test]
    fn tcp_eof_discards_an_unfinished_command_and_releases_client_threads() {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let mut client = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let stream = accept_client(&listener, false).unwrap().unwrap();
        let transport = Transport::start(stream).unwrap();

        client
            .write_all(b"{\"v\":1,\"id\":1,\"op\":\"status\"}\n")
            .unwrap();
        assert!(matches!(
            transport.incoming.recv_timeout(Duration::from_secs(1)),
            Ok(Incoming::Frame(Frame::Data(_)))
        ));
        client
            .write_all(b"{\"v\":1,\"id\":2,\"op\":\"write\"")
            .unwrap();
        client.shutdown(Shutdown::Write).unwrap();

        assert!(matches!(
            transport.incoming.recv_timeout(Duration::from_secs(1)),
            Ok(Incoming::Eof)
        ));
        assert!(transport.lost());
        transport.finish().unwrap();
    }

    #[test]
    fn closing_an_idle_client_reclaims_its_reader_without_peer_help() {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let mut client = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        client
            .set_read_timeout(Some(Duration::from_millis(500)))
            .unwrap();
        let transport =
            Transport::start(accept_client(&listener, false).unwrap().unwrap()).unwrap();

        assert_eq!(transport.finish(), Ok(()));
        let mut byte = [0];
        match client.read(&mut byte) {
            Ok(0) => {}
            Err(error) if error.kind() == io::ErrorKind::ConnectionReset => {}
            other => panic!("client socket stayed open after cleanup: {other:?}"),
        }
    }

    #[test]
    fn nonreading_client_cannot_keep_queued_output_or_io_threads_alive() {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let client = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let transport =
            Transport::start(accept_client(&listener, false).unwrap().unwrap()).unwrap();
        let frame = "x".repeat(MAX_FRAME_BYTES - 2);
        let mut saturated = false;
        for _ in 0..8192 {
            match transport.send(&frame) {
                Ok(()) => {}
                Err("client_backpressure") => {
                    saturated = true;
                    break;
                }
                Err(error) => panic!("output failed before saturation: {error}"),
            }
        }
        assert!(saturated, "output did not saturate");

        assert!(matches!(
            transport.finish(),
            Ok(()) | Err("client_write_failed")
        ));
        drop(client);
    }

    #[test]
    fn full_input_queue_cannot_delay_observing_client_loss() {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let mut client = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let transport =
            Transport::start(accept_client(&listener, false).unwrap().unwrap()).unwrap();
        let flood = b"{\"v\":1,\"id\":1,\"op\":\"status\"}\n".repeat(INPUT_CAPACITY + 2);
        client.write_all(&flood).unwrap();
        client.shutdown(Shutdown::Write).unwrap();

        let deadline = Instant::now() + Duration::from_secs(1);
        while !transport.lost() && Instant::now() < deadline {
            thread::yield_now();
        }
        let loss_observed = transport.lost();
        transport.finish().unwrap();
        assert!(
            loss_observed,
            "a full queue blocked the reader from detecting disconnect"
        );
    }
}
