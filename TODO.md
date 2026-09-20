# TODO

- Rename repo to OSSMDotNet
- Awesome assertions for test
- Maybe: fluent assertions for reusable validation rules
- Match stock firmware speed using fractional knob-percentage intervals;
  integer-only enumeration can reject valid truncated readings.
- Prevent an in-flight write completed after disconnect from restoring cleared
  speed-matching context.
- Reject simulator replies with missing or duplicate known JSON fields before
  deserialization defaults can fabricate state.
- Make `OssmDevice.IsExpectedStateImpl` non-public without reintroducing a
  separate speed-matcher type.
