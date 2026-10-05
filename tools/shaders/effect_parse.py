#!/usr/bin/env python3
"""Port of MojoShader's D3DX fx_2_0 effect parser to Python, big-endian (Xbox 360).

The XNA 3.0 Xbox 360 Effect payload is the standard D3DX 9.1 effect binary
(magic FE FF 09 01) but byte-swapped to big-endian.  We parse:
  header -> top-level parameters -> techniques/passes/states -> object tables.
The object table holds the compiled shader blobs (Xenos microcode).
"""
import struct, os, sys, json, hashlib

PAYLOAD_DIR = os.path.join(os.path.dirname(__file__), 'payloads')

SYMTYPE = {0:'void',1:'bool',2:'int',3:'float',4:'string',5:'texture',
           6:'texture1d',7:'texture2d',8:'texture3d',9:'texturecube',
           10:'sampler',11:'sampler1d',12:'sampler2d',13:'sampler3d',
           14:'samplercube',15:'pixelshader',16:'vertexshader',
           17:'pixelfragment',18:'vertexfragment',19:'unsupported'}
SYMCLASS = {0:'scalar',1:'vector',2:'matrix_rows',3:'matrix_columns',
            4:'object',5:'struct'}
SAMPLER_STATE = {0:'unknown0',1:'unknown1',2:'unknown2',3:'unknown3',4:'texture',
                 5:'addressU',6:'addressV',7:'addressW',8:'bordercolor',
                 9:'magfilter',10:'minfilter',11:'mipfilter',12:'mipmaplodbias',
                 13:'maxmiplevel',14:'maxanisotropy',15:'srgbtexture',
                 16:'elementindex',17:'dmapoffset'}
RS = {0:'ZENABLE',1:'FILLMODE',2:'SHADEMODE',3:'ZWRITEENABLE',4:'ALPHATESTENABLE',
      5:'LASTPIXEL',6:'SRCBLEND',7:'DESTBLEND',8:'CULLMODE',9:'ZFUNC',
      10:'ALPHAREF',11:'ALPHAFUNC',12:'DITHERENABLE',13:'ALPHABLENDENABLE',
      14:'FOGENABLE',15:'SPECULARENABLE',16:'FOGCOLOR',17:'FOGTABLEMODE',
      18:'FOGSTART',19:'FOGEND',20:'FOGDENSITY',21:'RANGEFOGENABLE',
      22:'STENCILENABLE',23:'STENCILFAIL',24:'STENCILZFAIL',25:'STENCILPASS',
      26:'STENCILFUNC',27:'STENCILREF',28:'STENCILMASK',29:'STENCILWRITEMASK',
      30:'TEXTUREFACTOR',31:'WRAP0',32:'WRAP1',33:'WRAP2',34:'WRAP3',35:'WRAP4',
      36:'WRAP5',37:'WRAP6',38:'WRAP7',39:'WRAP8',40:'WRAP9',41:'WRAP10',
      42:'WRAP11',43:'WRAP12',44:'WRAP13',45:'WRAP14',46:'WRAP15',47:'CLIPPING',
      48:'LIGHTING',49:'AMBIENT',50:'FOGVERTEXMODE',51:'COLORVERTEX',
      52:'LOCALVIEWER',53:'NORMALIZENORMALS',54:'DIFFUSEMATERIALSOURCE',
      55:'SPECULARMATERIALSOURCE',56:'AMBIENTMATERIALSOURCE',57:'EMISSIVEMATERIALSOURCE',
      58:'VERTEXBLEND',59:'CLIPPLANEENABLE',60:'POINTSIZE',61:'POINTSIZE_MIN',
      62:'POINTSPRITEENABLE',63:'POINTSCALEENABLE',64:'POINTSCALE_A',
      65:'POINTSCALE_B',66:'POINTSCALE_C',67:'MULTISAMPLEANTIALIAS',
      68:'MULTISAMPLEMASK',69:'PATCHEDGESTYLE',70:'DEBUGMONITORTOKEN',
      71:'POINTSIZE_MAX',72:'INDEXEDVERTEXBLENDENABLE',73:'COLORWRITEENABLE',
      74:'TWEENFACTOR',75:'BLENDOP',76:'POSITIONDEGREE',77:'NORMALDEGREE',
      78:'SCISSORTESTENABLE',79:'SLOPESCALEDEPTHBIAS',80:'ANTIALIASEDLINEENABLE',
      81:'MINTESSELLATIONLEVEL',82:'MAXTESSELLATIONLEVEL',83:'ADAPTIVETESS_X',
      84:'ADAPTIVETESS_Y',85:'ADAPTIVETESS_Z',86:'ADAPTIVETESS_W',
      87:'ENABLEADAPTIVETESSELLATION',88:'TWOSIDEDSTENCILMODE',89:'CCW_STENCILFAIL',
      90:'CCW_STENCILZFAIL',91:'CCW_STENCILPASS',92:'CCW_STENCILFUNC',
      93:'COLORWRITEENABLE1',94:'COLORWRITEENABLE2',95:'COLORWRITEENABLE3',
      96:'BLENDFACTOR',97:'SRGBWRITEENABLE',98:'DEPTHBIAS',99:'SEPARATEALPHABLENDENABLE',
      100:'SRCBLENDALPHA',101:'DESTBLENDALPHA',102:'BLENDOPALPHA',
      146:'VERTEXSHADER',147:'PIXELSHADER'}


class Reader:
    def __init__(self, data, base):
        self.data = data
        self.base = base
        self.p = 0

    def u32(self):
        v = struct.unpack_from('>I', self.data, self.p)[0]
        self.p += 4
        return v

    def take(self, n):
        v = self.data[self.p:self.p+n]
        self.p += n
        return v


def rdstr(data, base, off):
    if off == 0:
        return None
    ln = struct.unpack_from('>I', data, base + off)[0]
    if ln == 0:
        return ''
    s = data[base + off + 4: base + off + 4 + ln]
    return s.rstrip(b'\x00').decode('latin1')


def readvalue(data, base, typeoff, valoff, objects):
    """Returns a dict describing a value; side effect: sets objects[i]['type']."""
    tp = base + typeoff
    (vtype, vclass, nameoff, semoff, numel) = struct.unpack_from('>IIIII', data, tp)
    out = {'type': SYMTYPE.get(vtype, vtype), 'class': SYMCLASS.get(vclass, vclass),
           'name': rdstr(data, base, nameoff), 'semantic': rdstr(data, base, semoff),
           'elements': numel}
    if vclass in (0, 1, 2, 3):  # scalar/vector/matrix
        cols, rows = struct.unpack_from('>II', data, tp + 20)
        out.update(columns=cols, rows=rows)
        n = cols * rows * (numel if numel > 0 else 1)
        vals = struct.unpack_from('>%df' % n, data, base + valoff)
        out['value'] = list(vals)
    elif vclass == 4:  # object
        if vtype in (10, 11, 12, 13, 14):  # sampler -> states
            nstates = struct.unpack_from('>I', data, base + valoff)[0]
            states = []
            sp = base + valoff + 4
            for _ in range(nstates):
                stype, _fix, tob, vob = struct.unpack_from('>IIII', data, sp)
                sp += 16
                stype &= ~0xA0
                sub = readvalue(data, base, tob, vob, objects)
                states.append({'state': SAMPLER_STATE.get(stype, stype), 'value': sub})
            out['states'] = states
        else:
            n = numel if numel > 0 else 1
            idxs = list(struct.unpack_from('>%dI' % n, data, base + valoff))
            out['object_indices'] = idxs
            for i in idxs:
                objects.setdefault(i, {})['type'] = SYMTYPE.get(vtype, vtype)
    elif vclass == 5:  # struct
        mcount = struct.unpack_from('>I', data, tp + 20)[0]
        out['member_count'] = mcount
    return out


def readannotations(data, base, r, n, objects):
    annos = []
    for _ in range(n):
        tob = r.u32(); vob = r.u32()
        annos.append(readvalue(data, base, tob, vob, objects))
    return annos


def readparameters(data, base, r, n, objects):
    params = []
    for _ in range(n):
        tob = r.u32(); vob = r.u32(); _flags = r.u32(); nannos = r.u32()
        annos = readannotations(data, base, r, nannos, objects)
        val = readvalue(data, base, tob, vob, objects)
        val['annotations'] = annos
        params.append(val)
    return params


def readstates(data, base, r, n, objects):
    states = []
    for _ in range(n):
        stype = r.u32(); _fix = r.u32(); tob = r.u32(); vob = r.u32()
        states.append({'state': RS.get(stype, stype),
                       'value': readvalue(data, base, tob, vob, objects)})
    return states


def readpasses(data, base, r, n, objects):
    passes = []
    for _ in range(n):
        nameoff = r.u32(); nannos = r.u32(); nstates = r.u32()
        name = rdstr(data, base, nameoff)
        annos = readannotations(data, base, r, nannos, objects)
        states = readstates(data, base, r, nstates, objects)
        passes.append({'name': name, 'annotations': annos, 'states': states})
    return passes


def readtechniques(data, base, r, n, objects):
    techs = []
    for _ in range(n):
        nameoff = r.u32(); nannos = r.u32(); npasses = r.u32()
        name = rdstr(data, base, nameoff)
        annos = readannotations(data, base, r, nannos, objects)
        passes = readpasses(data, base, r, npasses, objects)
        techs.append({'name': name, 'annotations': annos, 'passes': passes})
    return techs


def parse(payload):
    data = payload
    magic, major, minor = struct.unpack_from('>HBB', data, 0)
    off = struct.unpack_from('>I', data, 4)[0]
    base = 8
    r = Reader(data, base)
    r.p = base + off
    numparams = r.u32(); numtech = r.u32(); fixme = r.u32(); numobj = r.u32()
    objects = {}
    params = readparameters(data, base, r, numparams, objects)
    techs = readtechniques(data, base, r, numtech, objects)
    numsmall = r.u32(); numlarge = r.u32()
    small = []
    for i in range(1, numsmall + 1):
        idx = r.u32(); length = r.u32()
        blob = data[r.p:r.p+length]
        small.append({'index': idx, 'length': length, 'blob': blob,
                      'type': objects.get(idx, {}).get('type')})
        blocklen = (length + 3) - ((length - 1) % 4)
        r.p += blocklen
    large = []
    for i in range(numsmall + 1, numsmall + numlarge + 1):
        technique, index, _fix, state, otype, length = struct.unpack_from('>IIIIII', data, r.p)
        r.p += 24
        blob = data[r.p:r.p+length]
        large.append({'technique': technique, 'pass': index, 'state': state,
                      'type': otype, 'length': length, 'blob': blob})
        blocklen = (length + 3) - ((length - 1) % 4)
        r.p += blocklen
    return {'magic': hex((magic << 16) | (major << 8) | minor),
            'offset': off, 'numparams': numparams, 'numtechniques': numtech,
            'fixme': fixme, 'numobjects': numobj, 'numsmall': numsmall,
            'numlarge': numlarge, 'params': params, 'techniques': techs,
            'objects': objects, 'small': small, 'large': large,
            'consumed': r.p, 'total': len(data)}


def main():
    names = sorted(f[:-4] for f in os.listdir(PAYLOAD_DIR) if f.endswith('.bin'))
    allinv = {}
    for nm in names:
        payload = open(os.path.join(PAYLOAD_DIR, nm + '.bin'), 'rb').read()
        try:
            inv = parse(payload)
        except Exception as e:
            print(f"!! {nm}: parse error {e!r} at ...")
            continue
        allinv[nm] = inv
        print(f"===== {nm}  payload={len(payload)} consumed={inv['consumed']} "
              f"params={inv['numparams']} tech={inv['numtechniques']} obj={inv['numobjects']} "
              f"small={inv['numsmall']} large={inv['numlarge']} =====")
        for p in inv['params']:
            extra = ''
            if 'value' in p:
                extra = ' val=' + str([round(x,4) for x in p['value'][:8]])
            if 'states' in p:
                extra = ' sampler-states=' + ','.join(str(s['state']) for s in p['states'])
            if 'object_indices' in p:
                extra = ' objref=' + str(p['object_indices'])
            print(f"    param {p['type']:9s}/{p['class']:9s} '{p['name']}' sem={p['semantic']!r} "
                  f"elem={p['elements']} cols={p.get('columns')} rows={p.get('rows')}{extra}")
        for t in inv['techniques']:
            passdesc = []
            for ps in t['passes']:
                st = [(s['state'], s['value'].get('name'), s['value'].get('object_indices'),
                       s['value'].get('semantic')) for s in ps['states']]
                passdesc.append((ps['name'], st))
            print(f"    technique '{t['name']}' passes={len(t['passes'])}: {passdesc}")
        for s in inv['small']:
            h = hashlib.sha256(s['blob']).hexdigest()[:16]
            print(f"    smallobj idx={s['index']} type={s['type']} len={s['length']} sha={h} "
                  f"head={s['blob'][:12].hex(' ')}")
        for s in inv['large']:
            h = hashlib.sha256(s['blob']).hexdigest()[:16]
            print(f"    largeobj tech={s['technique']} pass={s['pass']} state={s['state']} "
                  f"type={s['type']} len={s['length']} sha={h} head={s['blob'][:12].hex(' ')}")
    json.dump({k: {kk: vv for kk, vv in v.items() if kk not in ('small', 'large')}
               for k, v in allinv.items()}, open('/tmp/opencode/shader/inventory.json', 'w'),
              indent=1, default=str)


if __name__ == '__main__':
    main()
