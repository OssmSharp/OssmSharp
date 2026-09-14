use std::{fmt, num::NonZeroU64};

use serde::{
    Deserialize, Deserializer, Serialize,
    de::{MapAccess, Visitor, value::MapAccessDeserializer},
};

pub const MAX_FRAME_BYTES: usize = 4096;
pub const PROFILE_ID: &str = "ossm-rs-sim-v1";

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Desired {
    pub pattern: u32,
    pub speed: u32,
    pub stroke: u32,
    pub depth: u32,
    pub sensation: u32,
}

impl Default for Desired {
    fn default() -> Self {
        Self {
            pattern: 0,
            speed: 0,
            stroke: 10,
            depth: 20,
            sensation: 50,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Operation {
    Status,
    Write { command_id: u64, desired: Desired },
    Stop { command_id: u64 },
    External { desired: Desired },
    Delay { milliseconds: u64 },
    Stale { freeze: bool },
    Disconnect,
    Connect,
    Shutdown,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Request {
    pub id: u64,
    pub operation: Operation,
}

#[derive(Debug, Eq, PartialEq)]
pub struct Rejection {
    pub id: u64,
    pub code: &'static str,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Sample {
    pub sample_sequence: u64,
    pub change_revision: u64,
    pub command_id: Option<u64>,
    pub desired: Desired,
    pub enabled: bool,
    pub moving: bool,
    pub position: f64,
    pub velocity: f64,
    pub connected: bool,
    pub profile_id: &'static str,
}

#[derive(Debug, Serialize)]
pub struct Reply {
    pub v: u8,
    pub id: u64,
    pub ok: bool,
    pub error: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state: Option<Sample>,
}

#[derive(Debug, Serialize)]
pub struct Event {
    pub v: u8,
    pub event: &'static str,
    pub state: Sample,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WireRequest {
    v: u64,
    id: NonZeroU64,
    op: String,
    #[serde(default, deserialize_with = "present")]
    command_id: Option<NonZeroU64>,
    #[serde(default, deserialize_with = "present_desired_object")]
    desired: Option<Desired>,
    #[serde(default, deserialize_with = "present")]
    value: Option<u64>,
}

// Absent optional payloads are permitted; explicitly supplied null is not.
fn present<'de, D: Deserializer<'de>, T: Deserialize<'de>>(
    deserializer: D,
) -> Result<Option<T>, D::Error> {
    T::deserialize(deserializer).map(Some)
}

fn present_desired_object<'de, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<Option<Desired>, D::Error> {
    struct DesiredObject;

    impl<'de> Visitor<'de> for DesiredObject {
        type Value = Desired;

        fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
            formatter.write_str("a desired object")
        }

        fn visit_map<M: MapAccess<'de>>(self, map: M) -> Result<Desired, M::Error> {
            Desired::deserialize(MapAccessDeserializer::new(map))
        }
    }

    // Derived struct deserializers also accept arrays; this protocol does not.
    deserializer.deserialize_map(DesiredObject).map(Some)
}

pub fn parse_request(bytes: &[u8]) -> Result<Request, Rejection> {
    if bytes.len() > MAX_FRAME_BYTES {
        return Err(Rejection { id: 0, code: "frame_too_large" });
    }
    if bytes.iter().find(|byte| !byte.is_ascii_whitespace()) != Some(&b'{') {
        return Err(Rejection { id: 0, code: "invalid_request" });
    }

    // Recover correlation only from a complete JSON object with one valid ID.
    // The complete strict parse below still rejects unknown/duplicate fields.
    #[derive(Deserialize)]
    struct Correlation {
        id: NonZeroU64,
    }
    let id = serde_json::from_slice::<Correlation>(bytes)
        .map(|request| request.id.get())
        .unwrap_or(0);
    let reject = |code| Rejection { id, code };
    let wire: WireRequest =
        serde_json::from_slice(bytes).map_err(|_| reject("invalid_request"))?;
    if wire.v != 1 {
        return Err(reject("unsupported_version"));
    }

    let operation = match (wire.op.as_str(), wire.command_id, wire.desired, wire.value) {
        ("status", None, None, None) => Operation::Status,
        ("write", Some(command_id), Some(desired), None) => {
            validate_desired(desired, true).map_err(reject)?;
            Operation::Write { command_id: command_id.get(), desired }
        }
        ("stop", Some(command_id), None, None) => Operation::Stop { command_id: command_id.get() },
        ("external", None, Some(desired), None) => {
            validate_desired(desired, false).map_err(reject)?;
            Operation::External { desired }
        }
        ("delay", None, None, Some(milliseconds @ 0..=5000)) => Operation::Delay { milliseconds },
        ("stale", None, None, Some(value @ 0..=1)) => Operation::Stale { freeze: value == 1 },
        ("disconnect", None, None, None) => Operation::Disconnect,
        ("connect", None, None, None) => Operation::Connect,
        ("shutdown", None, None, None) => Operation::Shutdown,
        _ => return Err(reject("invalid_request")),
    };
    Ok(Request { id: wire.id.get(), operation })
}

fn validate_desired(desired: Desired, ordinary: bool) -> Result<(), &'static str> {
    if desired.pattern != 0
        || desired.speed > 100
        || desired.stroke > 100
        || desired.depth > 100
        || desired.sensation > 100
        || (ordinary && desired.sensation != 50)
        || desired.stroke > desired.depth
        || (desired.speed != 0 && desired.stroke == 0)
    {
        return Err("invalid_desired");
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{Value, json};

    fn parse(value: Value) -> Result<Request, Rejection> {
        parse_request(&serde_json::to_vec(&value).unwrap())
    }

    fn write() -> Value {
        json!({"v":1,"id":7,"op":"write","commandId":19,"desired":Desired::default()})
    }

    #[test]
    fn accepts_each_operation_with_its_exact_payload() {
        let cases = [
            (json!({"v":1,"id":1,"op":"status"}), Operation::Status),
            (write(), Operation::Write { command_id: 19, desired: Desired::default() }),
            (json!({"v":1,"id":1,"op":"stop","commandId":2}), Operation::Stop { command_id: 2 }),
            (json!({"v":1,"id":1,"op":"external","desired":Desired::default()}), Operation::External { desired: Desired::default() }),
            (json!({"v":1,"id":1,"op":"delay","value":5000}), Operation::Delay { milliseconds: 5000 }),
            (json!({"v":1,"id":1,"op":"stale","value":1}), Operation::Stale { freeze: true }),
            (json!({"v":1,"id":1,"op":"disconnect"}), Operation::Disconnect),
            (json!({"v":1,"id":1,"op":"connect"}), Operation::Connect),
            (json!({"v":1,"id":1,"op":"shutdown"}), Operation::Shutdown),
        ];
        for (input, expected) in cases {
            assert_eq!(parse(input).unwrap().operation, expected);
        }
    }

    #[test]
    fn rejects_unknown_duplicate_missing_and_null_fields() {
        let mut unknown = write();
        unknown["secret"] = json!("never echo this");
        let mut desired_unknown = write();
        desired_unknown["desired"]["extra"] = json!(1);
        let mut missing = write();
        missing["desired"].as_object_mut().unwrap().remove("depth");
        for input in [
            unknown,
            desired_unknown,
            missing,
            json!({"v":1,"id":7,"op":"status","commandId":null}),
            json!({"v":1,"id":7,"op":"status","desired":null}),
            json!({"v":1,"id":7,"op":"status","value":null}),
            json!({"v":1,"id":7,"op":"write","commandId":1}),
            json!({"v":1,"id":7,"op":"write","desired":Desired::default()}),
            json!({"v":1,"id":7,"op":"stop"}),
        ] {
            assert_eq!(parse(input).unwrap_err(), Rejection { id: 7, code: "invalid_request" });
        }
        assert!(parse_request(br#"{"v":1,"id":1,"op":"status","op":"connect"}"#).is_err());
        assert!(parse_request(br#"{"v":1,"id":1,"id":2,"op":"status"}"#).is_err());
        assert!(parse_request(br#"{"v":1,"id":1,"op":"external","desired":{"pattern":0,"speed":0,"speed":1,"stroke":10,"depth":20,"sensation":50}}"#).is_err());
    }

    #[test]
    fn rejects_structs_encoded_as_arrays_instead_of_json_objects() {
        for bytes in [
            br#"[1,7,"status"]"#.as_slice(),
            br#"{"v":1,"id":7,"op":"write","commandId":1,"desired":[0,0,10,20,50]}"#,
            br#"{"v":1,"id":7,"op":"external","desired":[0,0,10,20,50]}"#,
        ] {
            assert!(parse_request(bytes).is_err(), "request and desired must be objects");
        }
    }

    #[test]
    fn rejects_inappropriate_payloads_for_every_operation() {
        for op in ["status", "disconnect", "connect", "shutdown"] {
            for (key, value) in [("commandId", json!(1)), ("desired", json!(Desired::default())), ("value", json!(0))] {
                let mut input = json!({"v":1,"id":7,"op":op});
                input[key] = value;
                assert_eq!(parse(input).unwrap_err().code, "invalid_request");
            }
        }
        for input in [
            json!({"v":1,"id":7,"op":"write","commandId":1,"desired":Desired::default(),"value":0}),
            json!({"v":1,"id":7,"op":"stop","commandId":1,"desired":Desired::default()}),
            json!({"v":1,"id":7,"op":"stop","commandId":1,"value":0}),
            json!({"v":1,"id":7,"op":"external","desired":Desired::default(),"commandId":1}),
            json!({"v":1,"id":7,"op":"external","desired":Desired::default(),"value":1}),
            json!({"v":1,"id":7,"op":"delay","value":0,"desired":Desired::default()}),
            json!({"v":1,"id":7,"op":"stale","value":1,"commandId":1}),
        ] {
            assert_eq!(parse(input).unwrap_err().code, "invalid_request");
        }
    }

    #[test]
    fn accepts_only_positive_integer_ids_and_command_ids() {
        for invalid in [json!(0), json!(-1), json!(1.0), json!("1"), json!(null), json!(true)] {
            let mut input = write();
            input["id"] = invalid.clone();
            assert_eq!(parse(input).unwrap_err().id, 0);
            let mut input = write();
            input["commandId"] = invalid;
            assert_eq!(parse(input).unwrap_err().id, 7);
        }
        let mut input = write();
        input["id"] = json!(u64::MAX);
        input["commandId"] = json!(u64::MAX);
        assert_eq!(parse(input).unwrap().id, u64::MAX);
    }

    #[test]
    fn desired_requires_integer_percentages_and_supported_pattern() {
        for key in ["pattern", "speed", "stroke", "depth", "sensation"] {
            for invalid in [json!(-1), json!(1.5), json!(50.0), json!("50"), json!(null), json!(false), json!(101)] {
                let mut input = write();
                input["desired"][key] = invalid;
                assert!(parse(input).is_err(), "{key}");
            }
        }
        let mut input = write();
        input["desired"]["pattern"] = json!(1);
        assert_eq!(parse(input).unwrap_err().code, "invalid_desired");
    }

    #[test]
    fn validates_stroke_depth_and_the_zero_speed_boundary() {
        for (speed, stroke, depth, valid) in [
            (0, 0, 0, true),
            (100, 100, 100, true),
            (1, 1, 1, true),
            (1, 0, 100, false),
            (0, 10, 9, false),
            (100, 100, 99, false),
        ] {
            let mut input = write();
            input["desired"]["speed"] = json!(speed);
            input["desired"]["stroke"] = json!(stroke);
            input["desired"]["depth"] = json!(depth);
            assert_eq!(parse(input).is_ok(), valid);
        }
    }

    #[test]
    fn external_injection_alone_can_vary_sensation() {
        for sensation in [0, 49, 51, 100] {
            let mut input = write();
            input["desired"]["sensation"] = json!(sensation);
            assert_eq!(parse(input.clone()).unwrap_err().code, "invalid_desired");
            input["op"] = json!("external");
            input.as_object_mut().unwrap().remove("commandId");
            assert!(parse(input).is_ok());
        }
    }

    #[test]
    fn bounds_fault_values_and_rejects_unknown_versions_and_operations() {
        for input in [
            json!({"v":1,"id":7,"op":"delay","value":5001}),
            json!({"v":1,"id":7,"op":"delay","value":-1}),
            json!({"v":1,"id":7,"op":"delay","value":0.0}),
            json!({"v":1,"id":7,"op":"delay"}),
            json!({"v":1,"id":7,"op":"stale","value":2}),
            json!({"v":1,"id":7,"op":"stale","value":-1}),
            json!({"v":1,"id":7,"op":"stale","value":false}),
            json!({"v":1,"id":7,"op":"STATUS"}),
        ] {
            assert!(parse(input).is_err());
        }
        assert!(parse(json!({"v":1,"id":7,"op":"delay","value":0})).is_ok());
        assert!(parse(json!({"v":1,"id":7,"op":"stale","value":0})).is_ok());
        assert_eq!(parse(json!({"v":2,"id":7,"op":"status"})).unwrap_err(), Rejection { id: 7, code: "unsupported_version" });
    }

    #[test]
    fn malformed_utf8_json_and_overlong_frames_have_fixed_errors() {
        for bytes in [b"{".as_slice(), b"[]", b"null", b"\xff", b"{\"id\":1}\n{}"] {
            assert_eq!(parse_request(bytes).unwrap_err(), Rejection { id: 0, code: "invalid_request" });
        }
        assert_eq!(parse_request(br#"{"id":7}"#).unwrap_err().id, 7);
        assert_eq!(parse_request(&vec![b' '; MAX_FRAME_BYTES + 1]).unwrap_err(), Rejection { id: 0, code: "frame_too_large" });
        let mut exact = br#"{"v":1,"id":7,"op":"status"}"#.to_vec();
        exact.resize(MAX_FRAME_BYTES, b' ');
        assert!(parse_request(&exact).is_ok());
    }

    #[test]
    fn reply_and_event_have_only_the_contract_fields() {
        let sample = Sample {
            sample_sequence: 1, change_revision: 0, command_id: None,
            desired: Desired::default(), enabled: false, moving: false,
            position: 0.0, velocity: 0.0, connected: true, profile_id: PROFILE_ID,
        };
        let reply = serde_json::to_value(Reply { v: 1, id: 7, ok: true, error: None, state: Some(sample.clone()) }).unwrap();
        assert_eq!(reply.as_object().unwrap().len(), 5);
        assert_eq!(reply["state"], json!({
            "sampleSequence":1,"changeRevision":0,"commandId":null,
            "desired":{"pattern":0,"speed":0,"stroke":10,"depth":20,"sensation":50},
            "enabled":false,"moving":false,"position":0.0,"velocity":0.0,
            "connected":true,"profileId":"ossm-rs-sim-v1"
        }));
        let event = serde_json::to_value(Event { v: 1, event: "state", state: sample }).unwrap();
        assert_eq!(event.as_object().unwrap().len(), 3);
        assert_eq!(event["state"], reply["state"]);
        let failure = serde_json::to_value(Reply { v: 1, id: 0, ok: false, error: Some("invalid_request"), state: None }).unwrap();
        assert_eq!(failure, json!({"v":1,"id":0,"ok":false,"error":"invalid_request"}));
    }
}
