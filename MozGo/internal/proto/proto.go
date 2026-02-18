package proto

import (
	"encoding/binary"
	"errors"
)

const (
	MsgHello        = 0x01
	MsgHelloAck     = 0x11
	MsgData         = 0x02
	MsgKeepAlive    = 0x03
	MsgKeepAliveAck = 0x13
)

var ErrShort = errors.New("packet too short")

func BuildHello(session uint32, target string) []byte {
	targetLen := len(target)
	out := make([]byte, 1+4+2+targetLen)
	out[0] = MsgHello
	binary.BigEndian.PutUint32(out[1:], session)
	binary.BigEndian.PutUint16(out[5:], uint16(targetLen))
	copy(out[7:], []byte(target))
	return out
}

func ParseHello(b []byte) (session uint32, target string, err error) {
	if len(b) < 7 || b[0] != MsgHello {
		return 0, "", ErrShort
	}
	session = binary.BigEndian.Uint32(b[1:])
	tlen := int(binary.BigEndian.Uint16(b[5:]))
	if len(b) < 7+tlen {
		return 0, "", ErrShort
	}
	target = string(b[7 : 7+tlen])
	return session, target, nil
}

func BuildHelloAck(session uint32) []byte {
	out := make([]byte, 1+4)
	out[0] = MsgHelloAck
	binary.BigEndian.PutUint32(out[1:], session)
	return out
}

func BuildData(session uint32, payload []byte) []byte {
	out := make([]byte, 1+4+len(payload))
	out[0] = MsgData
	binary.BigEndian.PutUint32(out[1:], session)
	copy(out[5:], payload)
	return out
}

func ParseData(b []byte) (session uint32, payload []byte, err error) {
	if len(b) < 5 || b[0] != MsgData {
		return 0, nil, ErrShort
	}
	session = binary.BigEndian.Uint32(b[1:])
	payload = b[5:]
	return session, payload, nil
}

func BuildKeepAlive(session uint32) []byte {
	out := make([]byte, 1+4)
	out[0] = MsgKeepAlive
	binary.BigEndian.PutUint32(out[1:], session)
	return out
}

