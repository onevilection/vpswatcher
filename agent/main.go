// Command agent is the VPS監視-Mei server-side metrics agent: a stateless
// stream that emits one NDJSON sample per second to stdout (schema:
// docs/ndjson-schema.md + testdata/ fixtures). It is started over SSH (typically
// via an authorized_keys forced-command) and lives for the SSH session.
//
// Low-load rules (design §3.3) are enforced here:
//   - no external commands: every metric comes from /proc or statfs(2) directly;
//   - file descriptors for /proc/* are opened once and re-read with Seek(0);
//   - read buffer and the disk slice are reused across iterations (low GC);
//   - the 1Hz cadence is a sleep, not a busy loop;
//   - nothing is logged on the normal path.
package main

import (
	"bufio"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/onevilection/vpswatcher/agent/internal/metrics"
)

func main() {
	iface := flag.String("iface", "", "network interface to measure (default: auto from default route)")
	id := flag.String("id", "", "server identifier (required; must match servers.json id)")
	interval := flag.Int("interval", 1, "sampling interval in seconds")
	// --mounts is required to produce the per-mount disk array (schema §2.3);
	// the three flags in the design (§3.6) don't cover which filesystems to
	// report, so we accept a comma-separated list, defaulting to "/".
	mountsCSV := flag.String("mounts", "/", "comma-separated mount points to report")
	flag.Parse()

	if *id == "" {
		fmt.Fprintln(os.Stderr, "agent: --id is required")
		os.Exit(2)
	}
	if *interval < 1 {
		fmt.Fprintln(os.Stderr, "agent: --interval must be >= 1")
		os.Exit(2)
	}

	if err := run(*id, *iface, splitCSV(*mountsCSV), *interval); err != nil {
		fmt.Fprintln(os.Stderr, "agent:", err)
		os.Exit(1)
	}
}

// procFile wraps a /proc file kept open for the agent's lifetime; read() seeks
// to 0 and re-reads into a reused buffer (no per-loop open/alloc).
type procFile struct {
	f   *os.File
	buf []byte
}

func openProc(path string) (*procFile, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	return &procFile{f: f, buf: make([]byte, 0, 4096)}, nil
}

func (p *procFile) read() ([]byte, error) {
	if _, err := p.f.Seek(0, io.SeekStart); err != nil {
		return nil, err
	}
	p.buf = p.buf[:cap(p.buf)]
	n := 0
	for {
		if n == len(p.buf) {
			p.buf = append(p.buf, make([]byte, 4096)...) // grow once; reused next loop
		}
		m, err := p.f.Read(p.buf[n:])
		n += m
		if err == io.EOF || m == 0 {
			break
		}
		if err != nil {
			return nil, err
		}
	}
	return p.buf[:n], nil
}

func (p *procFile) close() { _ = p.f.Close() }

func run(id, ifaceFlag string, mounts []string, intervalSec int) error {
	stat, err := openProc("/proc/stat")
	if err != nil {
		return err
	}
	defer stat.close()
	meminfo, err := openProc("/proc/meminfo")
	if err != nil {
		return err
	}
	defer meminfo.close()
	netdev, err := openProc("/proc/net/dev")
	if err != nil {
		return err
	}
	defer netdev.close()
	loadavg, err := openProc("/proc/loadavg")
	if err != nil {
		return err
	}
	defer loadavg.close()
	uptime, err := openProc("/proc/uptime")
	if err != nil {
		return err
	}
	defer uptime.close()

	// Resolve the target interface once at startup (§3.4).
	iface := ifaceFlag
	if iface == "" {
		routeBytes, rerr := os.ReadFile("/proc/net/route")
		if rerr != nil {
			return rerr
		}
		iface, rerr = metrics.DefaultIface(routeBytes)
		if rerr != nil {
			return fmt.Errorf("could not determine default interface; pass --iface: %w", rerr)
		}
	}

	out := bufio.NewWriter(os.Stdout)
	enc := json.NewEncoder(out)
	enc.SetEscapeHTML(false)

	// Stop cleanly on SIGINT/SIGTERM (SSH session end also tears us down).
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	intervalDur := time.Duration(intervalSec) * time.Second
	intervalF := float64(intervalSec)

	var prevCPU metrics.CPUTimes
	var prevNet metrics.NetCounters
	hasPrev := false
	disks := make([]metrics.Disk, 0, len(mounts)) // reused each loop

	for {
		ts := time.Now().Unix()

		// CPU
		var cpuPct *float64
		curCPU := prevCPU
		if b, e := stat.read(); e == nil {
			if c, pe := metrics.ParseStatCPU(b); pe == nil {
				curCPU = c
				if hasPrev {
					cpuPct = metrics.ComputeCPU(prevCPU, curCPU)
				}
			}
		}

		// Memory + swap
		var mem metrics.Mem
		var swap metrics.Swap
		if b, e := meminfo.read(); e == nil {
			if mi, pe := metrics.ParseMeminfo(b); pe == nil {
				mem = metrics.ComputeMem(mi)
				swap = metrics.ComputeSwap(mi)
			}
		}

		// Disk (statfs per mount; never null, empty array allowed by schema)
		disks = disks[:0]
		for _, m := range mounts {
			var st syscall.Statfs_t
			if syscall.Statfs(m, &st) == nil {
				disks = append(disks, metrics.ComputeDisk(m, st.Blocks, st.Bfree, st.Bavail, uint64(st.Bsize)))
			}
		}

		// Network
		net := metrics.Net{Iface: iface}
		curNet := prevNet
		if b, e := netdev.read(); e == nil {
			if nc, pe := metrics.ParseNetDev(b, iface); pe == nil {
				curNet = nc
				if hasPrev {
					net.RxBps, net.TxBps = metrics.ComputeNet(prevNet, curNet, intervalF)
				}
			}
		}

		// Load + uptime
		load := []float64{0, 0, 0}
		if b, e := loadavg.read(); e == nil {
			if l, pe := metrics.ParseLoadavg(b); pe == nil {
				load = l
			}
		}
		var up int64
		if b, e := uptime.read(); e == nil {
			if u, pe := metrics.ParseUptime(b); pe == nil {
				up = u
			}
		}

		sample := metrics.Sample{
			V:         1,
			ID:        id,
			TS:        ts,
			CPUPct:    cpuPct,
			Mem:       mem,
			Swap:      swap,
			Disk:      disks,
			Net:       net,
			Load:      load,
			UptimeSec: up,
		}

		// Encode writes one line + '\n'; flush immediately (no buffering, §1).
		if err := enc.Encode(&sample); err != nil {
			return err
		}
		if err := out.Flush(); err != nil {
			return err
		}

		prevCPU = curCPU
		prevNet = curNet
		hasPrev = true

		// Sleep-based 1Hz; wake early on signal to exit cleanly.
		select {
		case <-sigCh:
			return nil
		case <-time.After(intervalDur):
		}
	}
}

// splitCSV splits a comma-separated list, trimming spaces and dropping empties.
func splitCSV(s string) []string {
	var out []string
	start := 0
	for i := 0; i <= len(s); i++ {
		if i == len(s) || s[i] == ',' {
			seg := trimSpace(s[start:i])
			if seg != "" {
				out = append(out, seg)
			}
			start = i + 1
		}
	}
	return out
}

func trimSpace(s string) string {
	for len(s) > 0 && (s[0] == ' ' || s[0] == '\t') {
		s = s[1:]
	}
	for len(s) > 0 && (s[len(s)-1] == ' ' || s[len(s)-1] == '\t') {
		s = s[:len(s)-1]
	}
	return s
}
