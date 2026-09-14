use std::{
    io::{self, Read, Write},
    sync::mpsc::{self, Receiver, SyncSender, TrySendError},
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
const OUTPUT_DRAIN_TIMEOUT: Duration = Duration::from_secs(2);

pub enum Incoming {
    Frame(Frame),
    Eof,
    Failed,
}

pub struct Transport {
    pub incoming: Receiver<Incoming>,
    outgoing: SyncSender<Vec<u8>>,
    reader: JoinHandle<()>,
    writer: JoinHandle<Result<(), &'static str>>,
}

impl Transport {
    pub fn start() -> Result<Self, &'static str> {
        let (input_tx, incoming) = mpsc::sync_channel(INPUT_CAPACITY);
        let (outgoing, output_rx) = mpsc::sync_channel::<Vec<u8>>(OUTPUT_CAPACITY);
        let reader = thread::Builder::new()
            .name("bridge-stdin".into())
            .spawn(move || read_input(io::stdin().lock(), input_tx))
            .map_err(|_| "stdin_thread_failed")?;
        let writer = thread::Builder::new()
            .name("bridge-stdout".into())
            .spawn(move || {
                let mut stdout = io::stdout().lock();
                for frame in output_rx {
                    stdout.write_all(&frame).map_err(|_| "stdout_failed")?;
                    stdout.flush().map_err(|_| "stdout_failed")?;
                }
                Ok(())
            })
            .map_err(|_| "stdout_thread_failed")?;
        Ok(Self { incoming, outgoing, reader, writer })
    }

    pub fn send(&self, message: &impl Serialize) -> Result<(), &'static str> {
        let mut bytes = serde_json::to_vec(message).map_err(|_| "serialization_failed")?;
        if bytes.len() > MAX_FRAME_BYTES {
            return Err("output_too_large");
        }
        bytes.push(b'\n');
        // Never stall the native engine waiting for a non-reading client.
        match self.outgoing.try_send(bytes) {
            Ok(()) => Ok(()),
            Err(TrySendError::Full(_)) => Err("stdout_backpressure"),
            Err(TrySendError::Disconnected(_)) => Err("stdout_task_failed"),
        }
    }

    pub fn check_writer(&self) -> Result<(), &'static str> {
        if self.writer.is_finished() {
            Err("stdout_task_failed")
        } else {
            Ok(())
        }
    }

    pub fn finish(self) -> Result<(), &'static str> {
        drop(self.incoming);
        drop(self.outgoing);
        let deadline = Instant::now() + OUTPUT_DRAIN_TIMEOUT;
        while !self.writer.is_finished() {
            if Instant::now() >= deadline {
                return Err("stdout_drain_timeout");
            }
            thread::sleep(Duration::from_millis(5));
        }
        self.writer.join().map_err(|_| "stdout_task_failed")??;
        // Stdin cannot portably be interrupted while the parent keeps its pipe
        // open. It is a process-lifetime OS thread, not a Tokio blocking task;
        // thus shutdown never waits indefinitely for stdin/runtime destruction.
        if self.reader.is_finished() {
            self.reader.join().map_err(|_| "stdin_task_failed")?;
        }
        Ok(())
    }
}

fn read_input(mut input: impl Read, tx: SyncSender<Incoming>) {
    let mut framer = Framer::default();
    let mut chunk = [0; 512];
    loop {
        match input.read(&mut chunk) {
            Ok(0) => {
                if let Some(frame) = framer.finish() {
                    if tx.send(Incoming::Frame(frame)).is_err() {
                        return;
                    }
                }
                let _ = tx.send(Incoming::Eof);
                return;
            }
            Ok(count) => {
                for &byte in &chunk[..count] {
                    if let Some(frame) = framer.push(byte) {
                        if tx.send(Incoming::Frame(frame)).is_err() {
                            return;
                        }
                    }
                }
            }
            Err(error) if error.kind() == io::ErrorKind::Interrupted => {}
            Err(_) => {
                let _ = tx.send(Incoming::Failed);
                return;
            }
        }
    }
}
