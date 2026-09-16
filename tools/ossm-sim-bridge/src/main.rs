mod engine;
mod transport;

use std::{
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
    protocol::{Event, Reply, parse_request},
    state::{Model, Transition},
};
use tokio::{runtime, task::JoinHandle};
use transport::{INPUT_CAPACITY, Incoming, Transport};

const SAMPLE_INTERVAL: Duration = Duration::from_millis(50);

fn main() {
    std::panic::set_hook(Box::new(|_| {
        static REPORTED: AtomicBool = AtomicBool::new(false);
        if !REPORTED.swap(true, Ordering::Relaxed) {
            eprintln!("ossm-sim-bridge: task_panic");
        }
    }));
    if std::env::args_os().len() != 1 {
        eprintln!("ossm-sim-bridge: no_arguments_expected; use JSON lines on stdin");
        std::process::exit(2);
    }
    let result = std::panic::catch_unwind(run).unwrap_or(Err("adapter_panic"));
    Engine::disable();
    if let Err(code) = result {
        eprintln!("ossm-sim-bridge: {code}");
        std::process::exit(1);
    }
}

fn run() -> Result<(), &'static str> {
    log::set_logger(&BOUNDED_LOG).map_err(|_| "logger_failed")?;
    log::set_max_level(log::LevelFilter::Error);
    let runtime = runtime::Builder::new_current_thread()
        .build()
        .map_err(|_| "runtime_failed")?;
    let transport = Transport::start()?;
    let result = runtime.block_on(run_adapter(&transport));
    let drained = transport.finish();
    result.and(drained)
}

async fn run_adapter(transport: &Transport) -> Result<(), &'static str> {
    let mut model = Model::default();
    let mut engine = Engine::new(model.controls);
    let motion = tokio::spawn(run_motion());
    let result = protocol_loop(transport, &mut model, &mut engine, &motion).await;
    Engine::disable();
    motion.abort();
    match motion.await {
        Err(error) if error.is_cancelled() => result,
        // run_motion is perpetual; both a panic and an unexpected return are failures.
        _ => Err("motion_task_failed"),
    }
}

async fn protocol_loop(
    transport: &Transport,
    model: &mut Model,
    engine: &mut Engine,
    motion: &JoinHandle<()>,
) -> Result<(), &'static str> {
    let mut ticker = embassy_time::Ticker::every(embassy_time::Duration::from_millis(
        MOTION_CONTROL_LOOP_UPDATE_INTERVAL_MS,
    ));
    engine.update()?;
    // A first request can enable stale mode, so sample the motion state before
    // accepting input. The sample comes from DebugOut and motion state, not a pose.
    model.sample(|| engine.observe())?;
    let mut next_event = Instant::now() + SAMPLE_INTERVAL;

    loop {
        if motion.is_finished() {
            return Err("motion_task_failed");
        }
        transport.check_writer()?;
        engine.update()?;

        // Bound work per tick so fault and status handling stay live while a
        // delayed ordinary command owns the one pending slot.
        for _ in 0..INPUT_CAPACITY {
            let frame = match transport.incoming.try_recv() {
                Ok(Incoming::Frame(frame)) => frame,
                Ok(Incoming::Eof) => return Ok(()),
                Ok(Incoming::Failed) => return Err("stdin_failed"),
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => return Err("stdin_task_failed"),
            };
            let parsed = match frame {
                Frame::Data(bytes) => parse_request(&bytes),
                Frame::TooLarge => Err(ossm_sim_bridge::protocol::Rejection {
                    id: 0,
                    code: "frame_too_large",
                }),
            };
            match parsed {
                Ok(request) => {
                    let transition = model.handle(request, Instant::now());
                    if emit_transition(transition, transport, model, engine)? {
                        return Ok(());
                    }
                }
                Err(rejection) => transport.send(&Reply {
                    v: 1,
                    id: rejection.id,
                    ok: false,
                    error: Some(rejection.code),
                    state: None,
                })?,
            }
        }
        if let Some(transition) = model.complete_due(Instant::now()) {
            emit_transition(transition, transport, model, engine)?;
        }
        let now = Instant::now();
        if now >= next_event {
            next_event = next_event_deadline(next_event, now);
            if !model.stale {
                transport.send(&Event {
                    v: 1,
                    event: "state",
                    state: model.sample(|| engine.observe())?,
                })?;
            }
        }
        ticker.next().await;
    }
}

fn next_event_deadline(previous: Instant, now: Instant) -> Instant {
    let next = previous + SAMPLE_INTERVAL;
    if next <= now {
        // Skip missed periods to avoid flooding stdout after a prolonged stall.
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
) -> Result<bool, &'static str> {
    if let Some(controls) = transition.apply {
        engine.apply(controls);
    }
    if let Some(outcome) = transition.outcome {
        transport.send(&Reply {
            v: 1,
            id: outcome.id,
            ok: outcome.error.is_none(),
            error: outcome.error,
            state: Some(model.sample(|| engine.observe())?),
        })?;
    }
    Ok(transition.shutdown)
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
