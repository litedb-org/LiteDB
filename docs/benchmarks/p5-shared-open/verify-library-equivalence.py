import subprocess,re,sys
left=sys.argv[1] if len(sys.argv)>1 else 'a09852383e4ebfdfd9aa67d27408024650037fba'
right=sys.argv[2] if len(sys.argv)>2 else '7000dd6f01d0187a8fb5d030064eb327864ab731'
def files(rev):return set(subprocess.check_output(['git','ls-tree','-r','--name-only',rev,'--','LiteDB'],text=True).splitlines())
def content(rev,path):
 try:s=subprocess.check_output(['git','show',rev+':'+path],stderr=subprocess.DEVNULL,text=True)
 except subprocess.CalledProcessError:return ''
 if not path.endswith('.cs'):return s
 depth=0;out=[]
 for line in s.splitlines():
  stripped=line.strip()
  if stripped=='#if PROFILE':
   assert depth==0
   depth=1;continue
  if depth:
   if stripped.startswith('#if '):depth+=1
   if stripped=='#endif':depth-=1
   continue
  if not stripped or stripped.startswith('//'):continue
  out.append(line.rstrip())
 assert depth==0
 return '\n'.join(out)
different=[p for p in sorted(files(left)|files(right)) if content(left,p)!=content(right,p)]
assert not different,different
print(f'PASS: {left} library equals {right}, excluding PROFILE-only blocks, blank lines and full-line comments; all non-C# library files compared literally.')
