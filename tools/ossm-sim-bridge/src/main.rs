mod engine;
mod transport;

use std::{
    ffi::{OsStr, OsString},
    io::Write,
    net::{Ipv4Addr, SocketAddrV4, TcpListener},
    sync::{
        atomic::{AtomicBool, AtomicUsize, Ordering},
        mpsc::TryRecvError,
    },
    time::{Duration, Instant},
};

use engine::Engine;
use ossm_motion::{config::MOTION_CONTROL_LOOP_UPDATE_INTERVAL_MS, motion::run_motion};
use ossm_sim_bridge::{
    framing::Frame,
    protocol::{Event, Operation, Reply, Request, parse_request},
    state::{Model, Transition},
};
use tokio::{runtime, task::JoinHandle};
use transport::{INPUT_CAPACITY, Incoming, Transport, accept_client};

const SAMPLE_INTERVAL: Duration = Duration::from_millis(50);

fn parse_listen(mut args: impl Iterator<Item = OsString>) -> Result<SocketAddrV4, &'static str> {
    if args.next().as_deref() != Some(OsStr::new("--listen")) {
        return Err("invalid_listen");
    }
    let address = args.next()
        .and_then(|value| value.into_string().ok())
        .and_then(|value| value.parse::<SocketAddrV4>().ok())
        .ok_or("invalid_listen")?;
    if address.ip() != &Ipv4Addr::LOCALHOST || address.port() == 0 || args.next().is_some() {
        return Err("invalid_listen");
    }
    Ok(address)
}

fn main() {
    std::panic::set_hook(Box::new(|_| {
        static REPORTED: AtomicBool = AtomicBool::new(false);
        if !REPORTED.swap(true, Ordering::Relaxed) {
            eprintln!("ossm-sim-bridge: task_panic");
        }
    }));
    let address = match parse_listen(std::env::args_os().skip(1)) {
        Ok(address) => address,
        Err(_) => {
            eprintln!("ossm-sim-bridge: use --listen 127.0.0.1:<port>");
            std::process::exit(2);
        }
    };
    let result = std::panic::catch_unwind(|| run(address)).unwrap_or(Err("adapter_panic"));
    Engine::disable();
    if let Err(code) = result {
        eprintln!("ossm-sim-bridge: {code}");
        std::process::exit(1);
    }
}

fn run(address: SocketAddrV4) -> Result<(), &'static str> {
    log::set_logger(&BOUNDED_LOG).map_err(|_| "logger_failed")?;
    log::set_max_level(log::LevelFilter::Error);
    let listener = TcpListener::bind(address).map_err(|_| "listen_failed")?;
    listener.set_nonblocking(true).map_err(|_| "listen_failed")?;
    let runtime = runtime::Builder::new_current_thread()
        .build()
        .map_err(|_| "runtime_failed")?;
    runtime.block_on(run_adapter(&listener))
}

async fn run_adapter(listener: &TcpListener) -> Result<(), &'static str> {
    let mut model = Model::default();
    model.handle(Request { id: 0, operation: Operation::Disconnect }, Instant::now());
    let mut engine = Engine::new(model.controls);
    let motion = tokio::spawn(run_motion());
    writeln!(std::io::stdout(), "ready").map_err(|_| "ready_failed")?;
    std::io::stdout().flush().map_err(|_| "ready_failed")?;
    let result = serve(listener, &mut model, &mut engine, &motion).await;
    Engine::disable();
    motion.abort();
    match motion.await {
        Err(error) if error.is_cancelled() => result,
        // run_motion is perpetual; both a panic and an unexpected return are failures.
        _ => Err("motion_task_failed"),
    }
}

async fn serve(
    listener: &TcpListener,
    model: &mut Model,
    engine: &mut Engine,
    motion: &JoinHandle<()>,
) -> Result<(), &'static str> {
    let mut ticker = embassy_time::Ticker::every(embassy_time::Duration::from_millis(
        MOTION_CONTROL_LOOP_UPDATE_INTERVAL_MS,
    ));
    engine.update()?;
    model.sample(|| engine.observe())?;
    let mut next_event = Instant::now() + SAMPLE_INTERVAL;
    let mut active: Option<Transport> = None;

    loop {
        if motion.is_finished() {
            return Err("motion_task_failed");
        }
        engine.update()?;
        serve_clients(listener, model, engine, &mut active, &mut next_event)?;

        ticker.next().await;
    }
}

fn serve_clients(
    listener: &TcpListener,
    model: &mut Model,
    engine: &Engine,
    active: &mut Option<Transport>,
    next_event: &mut Instant,
) -> Result<(), &'static str> {
    if let Some(transport) = active.as_ref() {
        match protocol_tick(transport, model, engine, next_event) {
            Ok(()) => {}
            Err(SessionError::Disconnected) => {
                let transition = model.handle(Request { id: 0, operation: Operation::Disconnect }, Instant::now());
                if let Some(controls) = transition.apply {
                    engine.apply(controls);
                }
                Engine::disable();
                client_cleanup_result(active.take().expect("active session checked").finish())?;
            }
            Err(SessionError::Fatal(code)) => return Err(code),
        }
    }

    for _ in 0..INPUT_CAPACITY {
        if let Some(stream) = accept_client(listener, active.is_some())? {
            if let Some(transport) = client_start_result(Transport::start(stream))? {
                let transition = model.handle(Request { id: 0, operation: Operation::Connect }, Instant::now());
                if let Some(controls) = transition.apply {
                    engine.apply(controls);
                }
                *next_event = Instant::now() + SAMPLE_INTERVAL;
                *active = Some(transport);
            }
        } else if active.is_none() {
            break;
        }
    }
    Ok(())
}

enum SessionError {
    Disconnected,
    Fatal(&'static str),
}

fn client_output_error(code: &'static str) -> SessionError {
    match code {
        "client_write_failed" | "client_backpressure" => SessionError::Disconnected,
        _ => SessionError::Fatal(code),
    }
}

fn client_start_result(result: Result<Transport, &'static str>) -> Result<Option<Transport>, &'static str> {
    match result {
        Ok(transport) => Ok(Some(transport)),
        Err("client_failed") => Ok(None),
        Err(code) => Err(code),
    }
}

fn client_cleanup_result(result: Result<(), &'static str>) -> Result<(), &'static str> {
    match result {
        Ok(()) | Err("client_write_failed") => Ok(()),
        Err(code) => Err(code),
    }
}

fn protocol_tick(
    transport: &Transport,
    model: &mut Model,
    engine: &Engine,
    next_event: &mut Instant,
) -> Result<(), SessionError> {
    if transport.lost() || transport.check_writer().is_err() {
        return Err(SessionError::Disconnected);
    }

    for _ in 0..INPUT_CAPACITY {
        if transport.lost() {
            return Err(SessionError::Disconnected);
        }
        let frame = match transport.incoming.try_recv() {
            Ok(Incoming::Frame(frame)) => frame,
            Ok(Incoming::Eof | Incoming::Failed) | Err(TryRecvError::Disconnected) => {
                return Err(SessionError::Disconnected);
            }
            Err(TryRecvError::Empty) => break,
        };
        let parsed = match frame {
            Frame::Data(bytes) => parse_request(&bytes),
            Frame::TooLarge => Err(ossm_sim_bridge::protocol::Rejection {
                id: 0,
                code: "frame_too_large",
            }),
        };
        if transport.lost() {
            return Err(SessionError::Disconnected);
        }
        match parsed {
            Ok(request) => {
                let transition = model.handle(request, Instant::now());
                emit_transition(transition, transport, model, engine)?;
            }
            Err(rejection) => transport.send(&Reply {
                v: 1,
                id: rejection.id,
                ok: false,
                error: Some(rejection.code),
                state: None,
            }).map_err(client_output_error)?,
        }
    }
    if transport.lost() {
        return Err(SessionError::Disconnected);
    }
    if let Some(transition) = model.complete_due(Instant::now()) {
        emit_transition(transition, transport, model, engine)?;
    }
    let now = Instant::now();
    if now >= *next_event {
        *next_event = next_event_deadline(*next_event, now);
        if !model.stale {
            transport.send(&Event {
                v: 1,
                event: "state",
                state: model.sample(|| engine.observe()).map_err(SessionError::Fatal)?,
            }).map_err(client_output_error)?;
        }
    }
    Ok(())
}

fn next_event_deadline(previous: Instant, now: Instant) -> Instant {
    let next = previous + SAMPLE_INTERVAL;
    if next <= now {
        // Skip missed periods to avoid flooding the client after a prolonged stall.
        now + SAMPLE_INTERVAL
    } else {
        next
    }
}

fn emit_transition(
    transition: Transition,
    transport: &Transport,
    model: &mut Model,
    engine: &Engine,
) -> Result<(), SessionError> {
    if let Some(controls) = transition.apply {
        engine.apply(controls);
    }
    if let Some(outcome) = transition.outcome {
        transport.send(&Reply {
            v: 1,
            id: outcome.id,
            ok: outcome.error.is_none(),
            error: outcome.error,
            state: Some(model.sample(|| engine.observe()).map_err(SessionError::Fatal)?),
        }).map_err(client_output_error)?;
    }
    Ok(())
}

struct BoundedLog(AtomicUsize);

static BOUNDED_LOG: BoundedLog = BoundedLog(AtomicUsize::new(0));

impl log::Log for BoundedLog {
    fn enabled(&self, metadata: &log::Metadata<'_>) -> bool {
        metadata.level() == log::Level::Error
    }

    fn log(&self, record: &log::Record<'_>) {
        if self.enabled(record.metadata())
            && self.0.fetch_update(Ordering::Relaxed, Ordering::Relaxed, |count| {
                (count < 4).then_some(count + 1)
            }).is_ok()
        {
            // Upstream diagnostics can repeat and include arbitrary formatting.
            // Do not echo them or request payloads on either stream.
            eprintln!("ossm-sim-bridge: upstream_error");
        }
    }

    fn flush(&self) {}
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn client_thread_start_failure_is_fatal() {
        assert!(matches!(client_start_result(Err("client_thread_failed")), Err("client_thread_failed")));
        assert!(matches!(client_start_result(Err("client_failed")), Ok(None)));
    }

    #[test]
    fn client_output_failures_only_disconnect_for_client_write_or_backpressure() {
        assert!(matches!(client_output_error("serialization_failed"), SessionError::Fatal("serialization_failed")));
        assert!(matches!(client_output_error("output_too_large"), SessionError::Fatal("output_too_large")));
        assert!(matches!(client_output_error("client_write_failed"), SessionError::Disconnected));
        assert!(matches!(client_output_error("client_backpressure"), SessionError::Disconnected));
    }

    #[test]
    fn saturated_output_evicts_nonreading_client_and_admits_successor() {
        use std::{io::{BufRead, BufReader, Write}, net::TcpStream, thread};
        use ossm_sim_bridge::protocol::MAX_FRAME_BYTES;
        use serde_json::Value;

        struct Active(Option<Transport>);
        impl Drop for Active {
            fn drop(&mut self) {
                if let Some(transport) = self.0.take() {
                    let _ = transport.finish();
                }
            }
        }

        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let address = listener.local_addr().unwrap();
        let _nonreading = TcpStream::connect(address).unwrap();
        let mut active = Active(Some(Transport::start(accept_client(&listener, false).unwrap().unwrap()).unwrap()));
        let mut model = Model::default();
        let engine = Engine::new(model.controls);
        let mut next_event = Instant::now();
        let frame = "x".repeat(MAX_FRAME_BYTES - 2);
        let saturation_deadline = Instant::now() + Duration::from_secs(2);
        let mut saturated = false;
        for _ in 0..8192 {
            match active.0.as_ref().unwrap().send(&frame) {
                Ok(()) => {}
                Err("client_backpressure") => { saturated = true; break; }
                Err(error) => panic!("output failed before saturation: {error}"),
            }
            assert!(Instant::now() < saturation_deadline, "output did not saturate");
        }
        assert!(saturated, "output did not saturate");
        assert!(!active.0.as_ref().unwrap().lost(), "client lost input before output saturated");

                        assert_eq!(client_cleanup_result(Err("client_cleanup_timeout")), Err("client_cleanup_timeout"));
                        assert_eq!(client_cleanup_result(Err("client_write_failed")), Ok(()));
        let eviction_deadline = Instant::now() + Duration::from_secs(2);
        while active.0.is_some() && Instant::now() < eviction_deadline {
            next_event = Instant::now();
            serve_clients(&listener, &mut model, &engine, &mut active.0, &mut next_event).unwrap();
            thread::yield_now();
        }
        assert!(active.0.is_none(), "server retained the non-reading client");
        assert!(!model.controls.connected);
        assert!(!model.controls.enabled);

        let mut successor = TcpStream::connect(address).unwrap();
        successor.write_all(b"{\"v\":1,\"id\":1,\"op\":\"status\"}\n").unwrap();
        successor.set_nonblocking(true).unwrap();
        let reply_deadline = Instant::now() + Duration::from_secs(2);
        loop {
            serve_clients(&listener, &mut model, &engine, &mut active.0, &mut next_event).unwrap();
            let mut first_byte = [0];
            if successor.peek(&mut first_byte).is_ok_and(|count| count > 0) {
                break;
            }
            assert!(Instant::now() < reply_deadline, "successor did not receive a reply");
            thread::yield_now();
        }
        successor.set_nonblocking(false).unwrap();
        successor.set_read_timeout(Some(Duration::from_secs(1))).unwrap();
        let mut line = String::new();
        BufReader::new(successor).read_line(&mut line).unwrap();
        let reply: Value = serde_json::from_str(&line).unwrap();
        assert_eq!(reply["id"], 1);
        assert_eq!(reply["state"]["connected"], true);
        assert_eq!(reply["state"]["enabled"], false);
        assert_eq!(reply["state"]["desired"]["speed"], 0);
    }

    #[test]
    fn unexpected_client_thread_failure_is_fatal() {
        assert_eq!(client_cleanup_result(Ok(())), Ok(()));
        assert_eq!(client_cleanup_result(Err("client_write_failed")), Ok(()));
        assert_eq!(client_cleanup_result(Err("client_thread_failed")), Err("client_thread_failed"));
        assert_eq!(client_cleanup_result(Err("client_cleanup_timeout")), Err("client_cleanup_timeout"));
    }

    #[test]
    fn listener_requires_explicit_ipv4_localhost_and_nonzero_port() {
        for args in [
            vec![],
            vec!["--listen"],
            vec!["--listen", "127.0.0.1:0"],
            vec!["--listen", "0.0.0.0:9000"],
            vec!["--listen", "192.168.1.10:9000"],
            vec!["--listen", "[::1]:9000"],
            vec!["--listen", "localhost:9000"],
            vec!["--listen", "127.0.0.1:9000", "extra"],
        ] {
            assert_eq!(parse_listen(args.into_iter().map(Into::into)), Err("invalid_listen"));
        }
        assert_eq!(
            parse_listen(["--listen", "127.0.0.1:9000"].into_iter().map(Into::into))
                .unwrap()
                .to_string(),
            "127.0.0.1:9000",
        );
    }

    #[test]
    fn a_late_tick_does_not_accumulate_periodic_sample_drift() {
        let start = Instant::now();
        assert_eq!(
            next_event_deadline(
                start + Duration::from_millis(50),
                start + Duration::from_millis(62),
            ),
            start + Duration::from_millis(100),
        );
    }

    #[test]
    fn missed_periods_skip_catchup_bursts() {
        let start = Instant::now();
        assert_eq!(
            next_event_deadline(
                start + Duration::from_millis(50),
                start + Duration::from_millis(240),
            ),
            start + Duration::from_millis(290),
        );
    }
}
