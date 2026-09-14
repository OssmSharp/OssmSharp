use std::time::{Duration, Instant};

use crate::protocol::{Desired, Operation, PROFILE_ID, Request, Sample};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Controls {
    pub desired: Desired,
    pub enabled: bool,
    pub connected: bool,
}

#[derive(Clone, Copy, Debug)]
pub struct Observation {
    pub desired: Desired,
    pub enabled: bool,
    pub moving: bool,
    pub position: f64,
    pub velocity: f64,
}

#[derive(Debug, Eq, PartialEq)]
pub struct Outcome {
    pub id: u64,
    pub error: Option<&'static str>,
}

#[derive(Debug)]
pub struct Transition {
    pub outcome: Option<Outcome>,
    pub apply: Option<Controls>,
    pub shutdown: bool,
}

pub struct Model {
    pub controls: Controls,
    pub command_id: Option<u64>,
    pub change_revision: u64,
    pub stale: bool,
    pending: Option<Pending>,
    next_delay: Duration,
    last_sample: Option<Sample>,
    sample_sequence: u64,
}

struct Pending {
    request: Request,
    due: Instant,
    disconnected: bool,
}

impl Default for Model {
    fn default() -> Self {
        Self {
            controls: Controls {
                desired: Desired::default(),
                enabled: false,
                connected: true,
            },
            command_id: None,
            change_revision: 0,
            stale: false,
            pending: None,
            next_delay: Duration::ZERO,
            last_sample: None,
            sample_sequence: 0,
        }
    }
}

impl Model {
    pub fn handle(&mut self, request: Request, now: Instant) -> Transition {
        if matches!(request.operation, Operation::Write { .. } | Operation::Stop { .. }) {
            if self.pending.is_some() {
                return Self::reject(request.id, "busy");
            }
            if !self.controls.connected {
                return Self::reject(request.id, "disconnected");
            }
            let delay = std::mem::take(&mut self.next_delay);
            if !delay.is_zero() {
                self.pending = Some(Pending { request, due: now + delay, disconnected: false });
                return Transition { outcome: None, apply: None, shutdown: false };
            }
        }
        self.apply_request(request)
    }

    pub fn complete_due(&mut self, now: Instant) -> Option<Transition> {
        if self.pending.as_ref().is_none_or(|pending| now < pending.due) {
            return None;
        }
        let pending = self.pending.take().expect("pending deadline checked");
        if pending.disconnected || !self.controls.connected {
            return Some(Self::reject(pending.request.id, "disconnected"));
        }
        // External changes do not cancel an already issued write. A stop has
        // no desired payload, so it preserves the controls at completion.
        Some(self.apply_request(pending.request))
    }

    pub fn sample(
        &mut self,
        observe: impl FnOnce() -> Observation,
    ) -> Result<Sample, &'static str> {
        if self.stale {
            return self.last_sample.clone().ok_or("missing_initial_sample");
        }
        let actual = observe();
        if !actual.position.is_finite() || !actual.velocity.is_finite() {
            return Err("non_finite_state");
        }
        self.sample_sequence = self.sample_sequence.checked_add(1).ok_or("sequence_exhausted")?;
        let sample = Sample {
            sample_sequence: self.sample_sequence,
            change_revision: self.change_revision,
            command_id: self.command_id,
            desired: actual.desired,
            enabled: actual.enabled,
            moving: actual.moving,
            position: actual.position,
            velocity: actual.velocity,
            connected: self.controls.connected,
            profile_id: PROFILE_ID,
        };
        self.last_sample = Some(sample.clone());
        Ok(sample)
    }

    fn apply_request(&mut self, request: Request) -> Transition {
        let before = self.controls;
        let mut shutdown = false;
        match request.operation {
            Operation::Status => {}
            Operation::Write { command_id, desired } => {
                self.controls.desired = desired;
                self.controls.enabled = desired.speed != 0;
                self.command_id = Some(command_id);
            }
            Operation::Stop { command_id } => {
                self.stop();
                self.command_id = Some(command_id);
            }
            Operation::External { desired } => {
                self.controls.desired = desired;
                self.controls.enabled = desired.speed != 0;
                self.command_id = None;
            }
            Operation::Delay { milliseconds } => {
                self.next_delay = Duration::from_millis(milliseconds);
            }
            Operation::Stale { freeze } => self.stale = freeze,
            Operation::Disconnect => {
                self.stop();
                self.controls.connected = false;
                self.command_id = None;
                if let Some(pending) = &mut self.pending {
                    // A reconnect cannot revive I/O issued on the old connection.
                    pending.disconnected = true;
                }
            }
            Operation::Connect => {
                self.controls = Controls {
                    desired: Desired::default(),
                    enabled: false,
                    connected: true,
                };
                self.command_id = None;
            }
            Operation::Shutdown => {
                self.stop();
                self.pending = None;
                shutdown = true;
            }
        }
        let apply = if before != self.controls {
            self.change_revision = self.change_revision.checked_add(1).expect("revision_exhausted");
            Some(self.controls)
        } else {
            None
        };
        Transition {
            outcome: Some(Outcome { id: request.id, error: None }),
            apply,
            shutdown,
        }
    }

    fn stop(&mut self) {
        self.controls.desired.speed = 0;
        self.controls.enabled = false;
    }

    fn reject(id: u64, code: &'static str) -> Transition {
        Transition {
            outcome: Some(Outcome { id, error: Some(code) }),
            apply: None,
            shutdown: false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::Operation;
    use std::time::Duration;

    fn request(model: &mut Model, id: u64, operation: Operation, now: Instant) -> Transition {
        model.handle(Request { id, operation }, now)
    }

    fn moving_desired() -> Desired {
        Desired { speed: 30, stroke: 20, depth: 40, ..Desired::default() }
    }

    fn observe(controls: Controls, moving: bool, position: f64) -> Observation {
        Observation { desired: controls.desired, enabled: controls.enabled, moving, position, velocity: if moving { 12.5 } else { 0.0 } }
    }

    fn sample(model: &mut Model) -> Sample {
        let controls = model.controls;
        model.sample(|| observe(controls, false, 10.0)).unwrap()
    }

    #[test]
    fn starts_connected_stopped_without_a_command_or_revision() {
        let mut model = Model::default();
        assert_eq!(model.controls, Controls { desired: Desired::default(), enabled: false, connected: true });
        assert_eq!(model.command_id, None);
        assert_eq!(model.change_revision, 0);
        assert!(!model.stale);
        assert_eq!(sample(&mut model).sample_sequence, 1);
    }

    #[test]
    fn own_writes_update_controls_but_duplicate_values_do_not_advance_revision() {
        let mut model = Model::default();
        let now = Instant::now();
        for command_id in [10, 11] {
            let transition = request(&mut model, command_id, Operation::Write { command_id, desired: moving_desired() }, now);
            assert_eq!(transition.outcome.unwrap(), Outcome { id: command_id, error: None });
            assert_eq!(model.command_id, Some(command_id));
            assert_eq!(model.controls.desired, moving_desired());
            assert!(model.controls.enabled);
            assert_eq!(model.change_revision, 1);
            assert_eq!(transition.apply.is_some(), command_id == 10);
        }
    }

    #[test]
    fn stop_preserves_external_controls_and_reports_real_retraction_as_moving() {
        let mut model = Model::default();
        let now = Instant::now();
        let external = Desired { sensation: 17, ..moving_desired() };
        request(&mut model, 1, Operation::External { desired: external }, now);
        assert_eq!(model.command_id, None);
        let transition = request(&mut model, 2, Operation::Stop { command_id: 9 }, now);
        assert_eq!(transition.apply.unwrap().desired, Desired { speed: 0, ..external });
        assert!(!model.controls.enabled);
        assert_eq!(model.command_id, Some(9));
        assert_eq!(model.change_revision, 2);
        let controls = model.controls;
        let stopped = model.sample(|| observe(controls, true, 45.0)).unwrap();
        assert!(!stopped.enabled);
        assert!(stopped.moving);
        assert_eq!(stopped.velocity, 12.5);
    }

    #[test]
    fn zero_speed_write_disables_and_external_clears_own_command_without_fake_revision() {
        let mut model = Model::default();
        let now = Instant::now();
        request(&mut model, 1, Operation::Write { command_id: 2, desired: Desired::default() }, now);
        assert!(!model.controls.enabled);
        assert_eq!(model.change_revision, 0);
        request(&mut model, 2, Operation::External { desired: Desired::default() }, now);
        assert_eq!(model.command_id, None);
        assert_eq!(model.change_revision, 0);
    }

    #[test]
    fn delayed_write_allows_status_external_and_faults_but_only_one_ordinary_command() {
        let mut model = Model::default();
        let now = Instant::now();
        request(&mut model, 1, Operation::Delay { milliseconds: 100 }, now);
        let pending = request(&mut model, 2, Operation::Write { command_id: 20, desired: moving_desired() }, now);
        assert!(pending.outcome.is_none());
        assert!(pending.apply.is_none());
        for operation in [Operation::Write { command_id: 21, desired: moving_desired() }, Operation::Stop { command_id: 22 }] {
            let busy = request(&mut model, 3, operation, now);
            assert_eq!(busy.outcome.unwrap().error, Some("busy"));
            assert!(busy.apply.is_none());
        }
        assert_eq!(request(&mut model, 4, Operation::Status, now).outcome.unwrap().error, None);
        let external = Desired { speed: 15, ..moving_desired() };
        request(&mut model, 5, Operation::External { desired: external }, now);
        assert_eq!(model.controls.desired, external);
        let before = sample(&mut model);
        assert_eq!(before.command_id, None);
        assert!(model.complete_due(now + Duration::from_millis(99)).is_none());
        let done = model.complete_due(now + Duration::from_millis(100)).unwrap();
        assert_eq!(done.outcome.unwrap(), Outcome { id: 2, error: None });
        assert_eq!(model.controls.desired, moving_desired());
        assert_eq!(model.command_id, Some(20));
        let after = sample(&mut model);
        assert!(after.sample_sequence > before.sample_sequence);
        assert!(model.complete_due(now + Duration::from_secs(1)).is_none());
        assert!(request(&mut model, 6, Operation::Stop { command_id: 23 }, now).outcome.is_some());
    }

    #[test]
    fn delayed_stop_preserves_controls_at_completion_not_a_stale_accepted_snapshot() {
        let mut model = Model::default();
        let now = Instant::now();
        request(&mut model, 1, Operation::Delay { milliseconds: 100 }, now);
        request(&mut model, 2, Operation::Stop { command_id: 3 }, now);
        let external = Desired { sensation: 77, ..moving_desired() };
        request(&mut model, 3, Operation::External { desired: external }, now);
        model.complete_due(now + Duration::from_millis(100)).unwrap();
        assert_eq!(model.controls.desired, Desired { speed: 0, ..external });
        assert!(!model.controls.enabled);
        assert_eq!(model.command_id, Some(3));
    }

    #[test]
    fn setting_another_delay_does_not_change_a_pending_deadline_or_consume_it_on_busy() {
        let mut model = Model::default();
        let now = Instant::now();
        request(&mut model, 1, Operation::Delay { milliseconds: 100 }, now);
        request(&mut model, 2, Operation::Write { command_id: 2, desired: moving_desired() }, now);
        request(&mut model, 3, Operation::Delay { milliseconds: 200 }, now);
        request(&mut model, 4, Operation::Stop { command_id: 4 }, now);
        assert!(model.complete_due(now + Duration::from_millis(100)).is_some());
        assert!(request(&mut model, 5, Operation::Stop { command_id: 5 }, now).outcome.is_none());
        assert!(model.complete_due(now + Duration::from_millis(199)).is_none());
        assert_eq!(model.complete_due(now + Duration::from_millis(200)).unwrap().outcome.unwrap().id, 5);
    }

    #[test]
    fn disconnect_invalidates_pending_io_even_if_reconnected_before_completion() {
        for reconnect in [false, true] {
            let mut model = Model::default();
            let now = Instant::now();
            request(&mut model, 1, Operation::Write { command_id: 1, desired: moving_desired() }, now);
            request(&mut model, 2, Operation::Delay { milliseconds: 100 }, now);
            request(&mut model, 3, Operation::Write { command_id: 3, desired: moving_desired() }, now);
            request(&mut model, 4, Operation::Disconnect, now);
            assert!(!model.controls.enabled);
            assert!(!model.controls.connected);
            assert_eq!(model.controls.desired.speed, 0);
            assert_eq!(model.command_id, None);
            if reconnect {
                request(&mut model, 5, Operation::Connect, now);
                assert!(model.controls.connected);
                assert!(!model.controls.enabled);
            }
            let revision = model.change_revision;
            let done = model.complete_due(now + Duration::from_millis(100)).unwrap();
            assert_eq!(done.outcome.unwrap(), Outcome { id: 3, error: Some("disconnected") });
            assert!(done.apply.is_none());
            assert_eq!(model.change_revision, revision);
            assert_eq!(model.command_id, None);
        }
    }

    #[test]
    fn disconnected_ordinary_writes_and_stops_do_not_mutate_controls() {
        let mut model = Model::default();
        let now = Instant::now();
        request(&mut model, 1, Operation::Disconnect, now);
        let before = model.controls;
        for operation in [
            Operation::Write { command_id: 2, desired: moving_desired() },
            Operation::Stop { command_id: 3 },
        ] {
            assert_eq!(request(&mut model, 2, operation, now).outcome.unwrap().error, Some("disconnected"));
            assert_eq!(model.controls, before);
            assert_eq!(model.change_revision, 1);
        }
        request(&mut model, 3, Operation::Disconnect, now);
        assert_eq!(model.change_revision, 1);
    }

    #[test]
    fn external_injection_changes_actual_controls_independently_of_connection_fault() {
        let mut model = Model::default();
        let now = Instant::now();
        request(&mut model, 1, Operation::Disconnect, now);
        let external = Desired { sensation: 72, ..moving_desired() };
        let injected = request(&mut model, 2, Operation::External { desired: external }, now);
        assert_eq!(injected.outcome.unwrap().error, None);
        assert_eq!(injected.apply.unwrap().desired, external);
        assert_eq!(model.controls.desired, external);
        assert!(!model.controls.connected);
        assert_eq!(model.command_id, None);
        assert_eq!(model.change_revision, 2);
    }

    #[test]
    fn connect_resets_all_controls_and_never_restores_motion() {
        let mut model = Model::default();
        let now = Instant::now();
        request(&mut model, 1, Operation::External { desired: Desired { sensation: 99, ..moving_desired() } }, now);
        request(&mut model, 2, Operation::Connect, now);
        assert_eq!(model.controls, Controls { desired: Desired::default(), enabled: false, connected: true });
        assert_eq!(model.command_id, None);
        assert_eq!(model.change_revision, 2);
        request(&mut model, 3, Operation::Connect, now);
        assert_eq!(model.change_revision, 2);
    }

    #[test]
    fn stale_freezes_the_entire_published_sample_not_controls_or_motion_observation() {
        let mut model = Model::default();
        let now = Instant::now();
        let original = sample(&mut model);
        request(&mut model, 1, Operation::Stale { freeze: true }, now);
        request(&mut model, 2, Operation::External { desired: moving_desired() }, now);
        assert!(model.controls.enabled);
        assert_eq!(model.change_revision, 1);
        for _ in 0..3 {
            assert_eq!(model.sample(|| panic!("stale mode must not refresh observation")).unwrap(), original);
        }
        request(&mut model, 3, Operation::Stale { freeze: false }, now);
        let fresh = sample(&mut model);
        assert_eq!(fresh.sample_sequence, original.sample_sequence + 1);
        assert_eq!(fresh.desired, moving_desired());
        assert_eq!(fresh.change_revision, 1);
    }

    #[test]
    fn delayed_completion_mutates_actual_controls_while_samples_are_frozen() {
        let mut model = Model::default();
        let now = Instant::now();
        let original = sample(&mut model);
        request(&mut model, 1, Operation::Delay { milliseconds: 50 }, now);
        request(&mut model, 2, Operation::Write { command_id: 3, desired: moving_desired() }, now);
        request(&mut model, 3, Operation::Stale { freeze: true }, now);
        assert_eq!(model.complete_due(now + Duration::from_millis(50)).unwrap().outcome.unwrap().error, None);
        assert!(model.controls.enabled);
        assert_eq!(model.command_id, Some(3));
        assert_eq!(model.sample(|| panic!("no refresh")).unwrap(), original);
    }

    #[test]
    fn samples_observed_engine_fields_without_fabricating_motion_or_control_revisions() {
        let mut model = Model::default();
        let observed = Controls { desired: moving_desired(), enabled: true, connected: true };
        let first = model.sample(|| observe(observed, true, 20.0)).unwrap();
        let second = model.sample(|| observe(observed, false, 21.0)).unwrap();
        assert_eq!(first.desired, moving_desired());
        assert!(first.enabled);
        assert!(first.moving);
        assert!(!second.moving);
        assert_eq!(second.position, 21.0);
        assert_eq!(second.sample_sequence, first.sample_sequence + 1);
        assert_eq!(second.change_revision, first.change_revision);
    }

    #[test]
    fn rejects_non_finite_engine_data_without_advancing_the_sample_sequence() {
        let mut model = Model::default();
        let original = sample(&mut model);
        for bad in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
            let controls = model.controls;
            assert_eq!(model.sample(|| observe(controls, false, bad)).unwrap_err(), "non_finite_state");
            assert_eq!(model.sample(|| Observation { velocity: bad, ..observe(controls, false, 10.0) }).unwrap_err(), "non_finite_state");
        }
        assert_eq!(sample(&mut model).sample_sequence, original.sample_sequence + 1);
    }

    #[test]
    fn shutdown_disables_without_waiting_for_a_pending_write() {
        let mut model = Model::default();
        let now = Instant::now();
        request(&mut model, 1, Operation::External { desired: moving_desired() }, now);
        request(&mut model, 2, Operation::Delay { milliseconds: 5000 }, now);
        request(&mut model, 3, Operation::Write { command_id: 3, desired: moving_desired() }, now);
        let transition = request(&mut model, 4, Operation::Shutdown, now);
        assert!(transition.shutdown);
        assert_eq!(transition.outcome.unwrap(), Outcome { id: 4, error: None });
        assert!(!model.controls.enabled);
        assert_eq!(model.controls.desired.speed, 0);
        assert!(model.complete_due(now + Duration::from_secs(5)).is_none());
    }
}
