package main

import (
	"flag"
	"fmt"
	"log"
	"net"
	"net/http"
	"sync"
	"time"

	"mozgo/internal/proto"
)

type session struct {
	id         uint32
	clientAddr *net.UDPAddr
	targetAddr *net.UDPAddr
	targetConn *net.UDPConn
	lastSeen   time.Time
}

func main() {
	httpAddr := flag.String("http", ":8080", "HTTP listen address")
	udpAddr := flag.String("udp", ":40000", "UDP listen address")
	flag.Parse()

	srv := &server{
		sessions: make(map[uint32]*session),
	}

	go func() {
		http.HandleFunc("/GetEP", func(w http.ResponseWriter, r *http.Request) {
			_, _ = fmt.Fprintf(w, "%s", r.RemoteAddr)
		})
		log.Printf("HTTP /GetEP on %s", *httpAddr)
		if err := http.ListenAndServe(*httpAddr, nil); err != nil {
			log.Fatal(err)
		}
	}()

	udp, err := net.ListenUDP("udp", mustResolveUDP(*udpAddr))
	if err != nil {
		log.Fatal(err)
	}
	defer udp.Close()
	log.Printf("UDP on %s", udp.LocalAddr().String())

	buf := make([]byte, 64*1024)
	for {
		n, addr, err := udp.ReadFromUDP(buf)
		if err != nil {
			log.Printf("udp read error: %v", err)
			continue
		}
		srv.handlePacket(udp, addr, buf[:n])
	}
}

type server struct {
	mu       sync.Mutex
	sessions map[uint32]*session
}

func (s *server) handlePacket(udp *net.UDPConn, addr *net.UDPAddr, b []byte) {
	switch b[0] {
	case proto.MsgHello:
		s.handleHello(udp, addr, b)
	case proto.MsgData:
		s.handleData(b)
	case proto.MsgKeepAlive:
		s.handleKeepAlive(udp, addr, b)
	default:
	}
}

func (s *server) handleHello(udp *net.UDPConn, addr *net.UDPAddr, b []byte) {
	sessionID, target, err := proto.ParseHello(b)
	if err != nil {
		return
	}
	tgtAddr, err := net.ResolveUDPAddr("udp", target)
	if err != nil {
		log.Printf("invalid target %q: %v", target, err)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	sess, ok := s.sessions[sessionID]
	if !ok {
		tconn, err := net.DialUDP("udp", nil, tgtAddr)
		if err != nil {
			log.Printf("dial target %s: %v", tgtAddr, err)
			return
		}
		sess = &session{
			id:         sessionID,
			clientAddr: addr,
			targetAddr: tgtAddr,
			targetConn: tconn,
			lastSeen:   time.Now(),
		}
		s.sessions[sessionID] = sess
		go s.readFromTarget(sessionID, tconn, udp)
	} else {
		sess.clientAddr = addr
		sess.lastSeen = time.Now()
	}

	_, _ = udp.WriteToUDP(proto.BuildHelloAck(sessionID), addr)
}

func (s *server) handleData(b []byte) {
	sessionID, payload, err := proto.ParseData(b)
	if err != nil {
		return
	}

	s.mu.Lock()
	sess := s.sessions[sessionID]
	if sess != nil {
		sess.lastSeen = time.Now()
	}
	s.mu.Unlock()
	if sess == nil {
		return
	}

	_, _ = sess.targetConn.Write(payload)
}

func (s *server) handleKeepAlive(udp *net.UDPConn, addr *net.UDPAddr, b []byte) {
	if len(b) < 5 {
		return
	}
	sessionID := binaryU32(b[1:])
	s.mu.Lock()
	if sess := s.sessions[sessionID]; sess != nil {
		sess.lastSeen = time.Now()
	}
	s.mu.Unlock()
	_, _ = udp.WriteToUDP(proto.BuildHelloAck(sessionID), addr)
}

func (s *server) readFromTarget(sessionID uint32, tgt *net.UDPConn, udp *net.UDPConn) {
	buf := make([]byte, 64*1024)
	for {
		n, err := tgt.Read(buf)
		if err != nil {
			return
		}
		s.mu.Lock()
		sess := s.sessions[sessionID]
		s.mu.Unlock()
		if sess == nil || sess.clientAddr == nil {
			continue
		}
		_, _ = udp.WriteToUDP(proto.BuildData(sessionID, buf[:n]), sess.clientAddr)
	}
}

func mustResolveUDP(addr string) *net.UDPAddr {
	udpAddr, err := net.ResolveUDPAddr("udp", addr)
	if err != nil {
		log.Fatal(err)
	}
	return udpAddr
}

func binaryU32(b []byte) uint32 {
	if len(b) < 4 {
		return 0
	}
	return uint32(b[0])<<24 | uint32(b[1])<<16 | uint32(b[2])<<8 | uint32(b[3])
}

