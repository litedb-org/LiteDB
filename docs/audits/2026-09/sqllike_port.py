# Faithful port of LiteDB/Utils/Extensions/StringExtensions.cs SqlLike (lines 45-167)
# Collation modelled as ordinal compare (c == p), i.e. Collation.Binary.
class OOR(Exception): pass
class Spin(Exception): pass

def sqllike(s, pattern, cap=100000):
    isMatch=True; isWildCardOn=False; isCharWildCardOn=False
    isCharSetOn=False; isNotCharSetOn=False; endOfPattern=False
    lastWildCard=-1; patternIndex=0; p='\0'
    i=0; steps=0
    while i < len(s):
        steps+=1
        if steps>cap: raise Spin(f"no termination after {cap} iterations")
        if i < 0: raise OOR(f"str[{i}] -> IndexOutOfRangeException")
        c=s[i]
        endOfPattern = (patternIndex >= len(pattern))
        if not endOfPattern:
            p = pattern[patternIndex]
            if (not isWildCardOn) and p=='%':
                lastWildCard = patternIndex
                isWildCardOn = True
                while patternIndex < len(pattern) and pattern[patternIndex]=='%':
                    patternIndex += 1
                p = '\0' if patternIndex >= len(pattern) else pattern[patternIndex]
            elif p=='_':
                isCharWildCardOn = True
                patternIndex += 1
        if isWildCardOn:
            if c==p:
                isWildCardOn=False; patternIndex+=1
        elif isCharWildCardOn:
            isCharWildCardOn=False
        elif isCharSetOn or isNotCharSetOn:
            if isCharSetOn:
                if lastWildCard>=0: patternIndex=lastWildCard
                else: isMatch=False; break
            isNotCharSetOn=isCharSetOn=False
        else:
            if c==p:
                patternIndex+=1
            else:
                if lastWildCard>=0:
                    back = patternIndex - lastWildCard - 1
                    i -= back
                    patternIndex = lastWildCard
                else:
                    isMatch=False; break
        i+=1
    endOfPattern = (patternIndex >= len(pattern))
    if isMatch and not endOfPattern:
        if all(ch=='%' for ch in pattern[patternIndex:]): endOfPattern=True
    return isMatch and endOfPattern
