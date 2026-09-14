use crate::protocol::MAX_FRAME_BYTES;

#[derive(Debug, Eq, PartialEq)]
pub enum Frame {
    Data(Vec<u8>),
    TooLarge,
}

pub struct Framer {
    bytes: Vec<u8>,
    overflow: bool,
}

impl Default for Framer {
    fn default() -> Self {
        Self {
            bytes: Vec::with_capacity(MAX_FRAME_BYTES + 1),
            overflow: false,
        }
    }
}

impl Framer {
    pub fn push(&mut self, byte: u8) -> Option<Frame> {
        if byte == b'\n' {
            return Some(self.take_frame());
        }
        // One extra byte allows a trailing CR without reducing the JSON limit.
        if self.bytes.len() < MAX_FRAME_BYTES + 1 {
            self.bytes.push(byte);
        } else {
            self.overflow = true;
        }
        None
    }

    pub fn finish(&mut self) -> Option<Frame> {
        if self.bytes.is_empty() && !self.overflow {
            None
        } else {
            Some(self.take_frame())
        }
    }

    fn take_frame(&mut self) -> Frame {
        if self.bytes.last() == Some(&b'\r') {
            self.bytes.pop();
        }
        let frame = if self.overflow || self.bytes.len() > MAX_FRAME_BYTES {
            Frame::TooLarge
        } else {
            Frame::Data(self.bytes.clone())
        };
        self.bytes.clear();
        self.overflow = false;
        frame
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed(framer: &mut Framer, bytes: &[u8]) -> Vec<Frame> {
        bytes.iter().filter_map(|&byte| framer.push(byte)).collect()
    }

    #[test]
    fn handles_multiple_lf_crlf_and_empty_lines() {
        assert_eq!(feed(&mut Framer::default(), b"{}\n{}\r\n\n"), [
            Frame::Data(b"{}".to_vec()), Frame::Data(b"{}".to_vec()), Frame::Data(vec![]),
        ]);
    }

    #[test]
    fn accepts_exact_limit_with_either_line_ending() {
        for ending in [b"\n".as_slice(), b"\r\n"] {
            let mut input = vec![b'x'; MAX_FRAME_BYTES];
            input.extend_from_slice(ending);
            assert_eq!(feed(&mut Framer::default(), &input), [Frame::Data(vec![b'x'; MAX_FRAME_BYTES])]);
        }
    }

    #[test]
    fn overlong_frame_is_drained_once_with_bounded_storage_then_recovers() {
        let mut framer = Framer::default();
        for _ in 0..100_000 {
            assert_eq!(framer.push(b'x'), None);
            assert!(framer.bytes.len() <= MAX_FRAME_BYTES + 1);
            assert!(framer.bytes.capacity() <= MAX_FRAME_BYTES + 1);
        }
        assert_eq!(feed(&mut framer, b"\n{}\n"), [Frame::TooLarge, Frame::Data(b"{}".to_vec())]);
    }

    #[test]
    fn eof_processes_a_final_frame_but_not_an_extra_empty_frame() {
        let mut framer = Framer::default();
        assert!(feed(&mut framer, b"{}").is_empty());
        assert_eq!(framer.finish(), Some(Frame::Data(b"{}".to_vec())));
        assert_eq!(framer.finish(), None);
        assert!(feed(&mut framer, &vec![b'x'; MAX_FRAME_BYTES + 1]).is_empty());
        assert_eq!(framer.finish(), Some(Frame::TooLarge));
        assert_eq!(framer.finish(), None);
    }
}
