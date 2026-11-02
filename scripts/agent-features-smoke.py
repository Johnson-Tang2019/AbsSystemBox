# SPDX-License-Identifier: GPL-3.0-or-later
"""Exercise only the new CLI/MCP transport with isolated Debug storage."""
from pathlib import Path
import json, os, subprocess, uuid, queue, threading

root=Path(__file__).resolve().parent.parent
out=root/'artifacts'/'agent-smoke'/uuid.uuid4().hex
out.mkdir(parents=True)
env=os.environ.copy()
env.update(SYSBOX_TEST_RULE_DIRECTORY=str(out/'rules'),SYSBOX_TEST_INDEX_DIRECTORY=str(out/'index'),SYSBOX_TEST_PIPE_SUFFIX=uuid.uuid4().hex)
cli=root/'src/SysBox.Cli/bin/Debug/net10.0-windows/sysbox.exe'
mcp=root/'src/SysBox.Mcp/bin/Debug/net10.0-windows/SysBox.Mcp.exe'
checks=[]
host_pid=None
def command(*args, expected=0):
    result=subprocess.run([str(cli),*args,'--json'],env=env,capture_output=True,text=True,encoding='utf-8',timeout=25)
    assert result.returncode==expected,(args,result.stdout,result.stderr)
    body=json.loads(result.stdout)
    assert body['ok']==(expected==0),body
    return body

process=None
try:
    state=command('status')['data'];host_pid=state['processId']
    assert state['mode']=='user' and not state['cleanup']['policy']['enabled']
    memory=command('memory','status')['data'];assert memory['value']['totalPhysicalBytes']>0,memory
    command('cleanup','configure',expected=2)
    command('rules','approve',expected=2)
    command('search','x','--limit','201',expected=2)
    command('storage','analyze',str(out),expected=3)
    checks.append('CLI returns real memory and versioned JSON; rejects unknown approval/cleanup commands, output limits and unapproved scopes')
    errors=open(out/'mcp-stderr.txt','w',encoding='utf-8')
    process=subprocess.Popen([str(mcp)],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=errors,text=True,encoding='utf-8',bufsize=1)
    messages=queue.Queue()
    def read():
        for line in process.stdout:
            try:messages.put(json.loads(line))
            except Exception:messages.put({'invalidStdout':line})
    threading.Thread(target=read,daemon=True).start()
    def send(value):
        process.stdin.write(json.dumps(value)+'\n');process.stdin.flush()
    def request(identifier,method,params):
        send({'jsonrpc':'2.0','id':identifier,'method':method,'params':params})
        while True:
            answer=messages.get(timeout=20)
            assert 'invalidStdout' not in answer,answer
            if answer.get('id')==identifier:
                assert 'error' not in answer,answer
                return answer['result']
    request(1,'initialize',{'protocolVersion':'2025-06-18','capabilities':{},'clientInfo':{'name':'SysBox-new-feature-fixture','version':'1.0'}})
    send({'jsonrpc':'2.0','method':'notifications/initialized'})
    listing=request(2,'tools/list',{})
    names=[tool['name'] for tool in listing['tools']]
    assert len(names)==12,names
    assert not any(any(word in name for word in ['approve','configure','execute','delete','scan']) for name in names),names
    reading=request(3,'tools/call',{'name':'memory_status','arguments':{}})
    assert not reading.get('isError'),reading
    decoded=json.loads(reading['content'][0]['text']);assert decoded['value']['totalPhysicalBytes']>0,decoded
    cleanup=request(4,'tools/call',{'name':'cleanup_status','arguments':{}})
    assert not cleanup.get('isError'),cleanup
    checks.append('Official MCP SDK negotiates stdio, lists twelve bounded tools, returns real memory and cleanup status with no non-protocol stdout')
    (out/'agent-flow.json').write_text(json.dumps({'checks':checks,'tools':names},indent=2),encoding='utf-8')
    print(json.dumps({'report':str(out/'agent-flow.json'),'checks':checks},ensure_ascii=False))
finally:
    if process is not None:
        process.stdin.close()
        try:process.wait(timeout=5)
        except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
    if host_pid:
        # This PID was returned by our GUID-isolated host; never stop an installed user host.
        subprocess.run(['taskkill.exe','/PID',str(host_pid),'/F'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
