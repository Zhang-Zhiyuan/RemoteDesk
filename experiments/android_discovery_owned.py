"""Owned emulator/localhost UDP fixture; no authentication, OS input or network discovery outside Android product's normal bounded scan."""
import argparse,json,pathlib,socket,subprocess,threading,time
p=argparse.ArgumentParser();p.add_argument('--output',required=True);a=p.parse_args()
out=pathlib.Path(a.output);out.mkdir(parents=True,exist_ok=False)
adb=pathlib.Path.home()/'AppData/Local/Android/Sdk/platform-tools/adb.exe';package='com.remotedesk.viewerprobe'
def run(*args):return subprocess.run([str(adb),'-s','emulator-5586',*args],capture_output=True,check=True,timeout=25).stdout.decode('utf-8').strip()
assert 'RemoteDeskRows_20261004_r1' in run('emu','avd','name').splitlines()
stop=threading.Event();requests=[]
udp=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);udp.bind(('127.0.0.1',40566));udp.settimeout(.2)
def reply():
    while not stop.is_set():
        try:data,address=udp.recvfrom(256)
        except socket.timeout:continue
        except OSError:return
        if data!=b'RemoteDesk.Discover.v1':continue
        requests.append(address[0])
        udp.sendto(json.dumps(dict(Type='RemoteDesk.Discover.Response.v1',MachineName='Owned discovery fixture',Platform='Windows',Port=45678,IsHostRunning=True,Address='203.0.113.99')).encode(),address)
worker=threading.Thread(target=reply,daemon=True);worker.start()
try:
    run('shell','am','force-stop',package)
    run('shell','run-as',package,'rm','-f','files/discovery-state.json')
    run('shell','am','start','-W','-n',package+'/com.remotedesk.agent.DiscoveryProbeActivity','--ez','externalOwnedFixture','true')
    deadline=time.monotonic()+25;report={}
    while time.monotonic()<deadline:
        try:report=json.loads(run('shell','run-as',package,'cat','files/discovery-state.json'))
        except (subprocess.CalledProcessError,ValueError):pass
        if report.get('completed') or report.get('failure'):break
        time.sleep(.3)
    report['ownedUdpRequests']=len(requests)
    (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    assert report.get('completed') and requests and all(c['passed'] for c in report['checks']),report.get('failure','incomplete discovery')
    print(json.dumps(dict(completed=True,checks=len(report['checks']),udpRequests=len(requests))))
finally:
    stop.set();udp.close();worker.join(1);run('shell','am','force-stop',package)
