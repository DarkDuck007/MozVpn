package main

import (
	"flag"
	"fmt"
	"log"
	"math/rand"
	"net"
	"net/http"
	"os"
	"sync"
	"time"

	"mozgo/internal/proto"
)

func main() {
	serverHTTP := flag.String("server-http", "", "Server HTTP base (e.g. http://host:8080)")
	serverUDP := flag.String("server-udp", "", "Server UDP addr (e.g. host:40000)")
	localListen := flag.String("local", "127.0.0.1:1081", "Local UDP listen addr")
	target := flag.String("target", "", "Target UDP endpoint host:port")
	flag.Parse()

	if *serverUDP == "" || *target == "" {
		fmt.Fprintln(os.Stderr, "server-udp and target are required")
		os.Exit(2)
	}

	if *serverHTTP != "" {
		getEP(*serverHTTP)
	}

	udpServer, err := net.ResolveUDPAddr("udp", *serverUDP)
	if err != nil {
		log.Fatal(err)
	}

	localAddr := mustResolveUDP(*localListen)
	localConn, err := net.ListenUDP("udp", localAddr)
	if err != nil {
		log.Fatal(err)
	}
	defer localConn.Close()

	serverConn, err := net.ListenUDP("udp", nil)
	if err != nil {
		log.Fatal(err)
	}
	defer serverConn.Close()

	sessionID := rand.New(rand.NewSource(time.Now().UnixNano())).Uint32()
	hello := proto.BuildHello(sessionID, *target)
	if _, err := serverConn.WriteToUDP(hello, udpServer); err != nil {
		log.Fatal(err)
	}

	log.Printf("session %d local %s -> server %s target %s", sessionID, localAddr, udpServer, *target)

	lastLocal := &lastAddr{}
	go readFromServer(sessionID, serverConn, localConn, lastLocal)
	go keepAlive(sessionID, serverConn, udpServer)

	forwardFromLocal(sessionID, localConn, serverConn, udpServer, lastLocal)
}

func getEP(serverHTTP string) {
	resp, err := http.Get(serverHTTP + "/GetEP")
	if err != nil {
		log.Printf("GetEP error: %v", err)
		return
	}
	defer resp.Body.Close()
	buf := make([]byte, 256)
	n, _ := resp.Body.Read(buf)
	log.Printf("GetEP: %s", string(buf[:n]))
}

func forwardFromLocal(session uint32, localConn *net.UDPConn, serverConn *net.UDPConn, server *net.UDPAddr, lastLocal *lastAddr) {
	buf := make([]byte, 64*1024)
	for {
		n, addr, err := localConn.ReadFromUDP(buf)
		if err != nil {
			log.Printf("local read error: %v", err)
			continue
		}
		lastLocal.Set(addr)
		pkt := proto.BuildData(session, buf[:n])
		_, _ = serverConn.WriteToUDP(pkt, server)
	}
}

func readFromServer(session uint32, serverConn *net.UDPConn, localConn *net.UDPConn, lastLocal *lastAddr) {
	buf := make([]byte, 64*1024)
	for {
		n, _, err := serverConn.ReadFromUDP(buf)
		if err != nil {
			log.Printf("server read error: %v", err)
			continue
		}
		if n < 5 || buf[0] != proto.MsgData {
			continue
		}
		recvSession, payload, err := proto.ParseData(buf[:n])
		if err != nil || recvSession != session {
			continue
		}
		if addr := lastLocal.Get(); addr != nil {
			_, _ = localConn.WriteToUDP(payload, addr)
		}
	}
}

func keepAlive(session uint32, serverConn *net.UDPConn, server *net.UDPAddr) {
	t := time.NewTicker(10 * time.Second)
	defer t.Stop()
	for range t.C {
		_, _ = serverConn.WriteToUDP(proto.BuildKeepAlive(session), server)
	}
}

type lastAddr struct {
	mu   sync.Mutex
	addr *net.UDPAddr
}

func (l *lastAddr) Set(addr *net.UDPAddr) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if addr == nil {
		l.addr = nil
		return
	}
	cpy := *addr
	l.addr = &cpy
}

func (l *lastAddr) Get() *net.UDPAddr {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.addr == nil {
		return nil
	}
	cpy := *l.addr
	return &cpy
}

func mustResolveUDP(addr string) *net.UDPAddr {
	udpAddr, err := net.ResolveUDPAddr("udp", addr)
	if err != nil {
		log.Fatal(err)
	}
	return udpAddr
}
