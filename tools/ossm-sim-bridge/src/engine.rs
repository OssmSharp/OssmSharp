use std::{cell::Cell, rc::Rc};

use ossm_motion::{
    motion::motion_state,
    motion_control::{
        self, MotionControl,
        debug::DebugOut,
        motor::Motor,
        timer::{Timer, TimerDuration, TimerInstant},
    },
};
use ossm_sim_bridge::{
    protocol::Desired,
    state::{Controls, Observation},
};

pub struct Engine {
    control: MotionControl<SimMotor, SimTimer, SimDebug>,
    debug: Rc<Kinematics>,
}

impl Engine {
    pub fn new(controls: Controls) -> Self {
        let debug = Rc::new(Kinematics::default());
        let engine = Self {
            control: MotionControl::new_with_debug(SimMotor, SimTimer, SimDebug(debug.clone())),
            debug,
        };
        engine.apply(controls);
        engine
    }

    pub fn update(&mut self) -> Result<(), &'static str> {
        self.control.update_handler();
        if !self.debug.position.get().is_finite() || !self.debug.velocity.get().is_finite() {
            return Err("non_finite_state");
        }
        Ok(())
    }

    pub fn apply(&self, controls: Controls) {
        let current = motion_state::get_motion_state();
        let desired = controls.desired;
        if !controls.enabled {
            motion_state::set_motion_enabled(false);
        }
        if current.pattern != desired.pattern {
            motion_state::set_motion_pattern(desired.pattern);
        }
        if current.depth != desired.depth {
            motion_state::set_motion_depth_pct(desired.depth);
        }
        if current.motion_length != desired.stroke {
            motion_state::set_motion_length_pct(desired.stroke);
        }
        if current.sensation != desired.sensation {
            motion_state::set_motion_sensation_pct(desired.sensation);
        }
        if current.velocity != desired.speed {
            motion_state::set_motion_velocity_pct(desired.speed);
        }
        // This runs without an await on the same Tokio thread as run_motion.
        // The native task never observes half-applied control fields.
        motion_state::set_motion_enabled(controls.enabled);
    }

    pub fn observe(&self) -> Observation {
        let actual = motion_state::get_motion_state();
        Observation {
            desired: Desired {
                pattern: actual.pattern,
                speed: actual.velocity,
                stroke: actual.motion_length,
                depth: actual.depth,
                sensation: actual.sensation,
            },
            enabled: actual.motion_enabled,
            moving: motion_control::is_move_in_progress(),
            position: self.debug.position.get(),
            velocity: self.debug.velocity.get(),
        }
    }

    pub fn disable() {
        motion_state::set_motion_enabled(false);
        if motion_state::get_motion_state().velocity != 0 {
            motion_state::set_motion_velocity_pct(0);
        }
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        Self::disable();
    }
}

struct SimMotor;

impl Motor for SimMotor {
    type MotorError = ();

    fn min_consecutive_write_delay() -> TimerDuration {
        TimerDuration::millis(1)
    }

    fn set_absolute_position(&mut self, _steps: i32) -> Result<(), Self::MotorError> {
        Ok(())
    }

    fn set_max_allowed_output(&mut self, _output: u16) -> Result<(), Self::MotorError> {
        Ok(())
    }

    // Like ossm-sim's dummy motor, this never sleeps or accesses a device.
    fn delay(&mut self, _duration: TimerDuration) {}
}

struct SimTimer;

impl Timer for SimTimer {
    fn now(&self) -> TimerInstant {
        TimerInstant::from_ticks(embassy_time::Instant::now().as_micros())
    }
}

#[derive(Default)]
struct Kinematics {
    position: Cell<f64>,
    velocity: Cell<f64>,
}

struct SimDebug(Rc<Kinematics>);

impl DebugOut for SimDebug {
    fn new_position(&mut self, position: f64) {
        self.0.position.set(position);
    }

    fn new_velocity(&mut self, velocity: f64) {
        self.0.velocity.set(velocity);
    }

    fn new_acceleration(&mut self, _acceleration: f64) {}

    fn new_jerk(&mut self, _jerk: f64) {}
}
