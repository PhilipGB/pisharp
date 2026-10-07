"""Exact terminal-state comparison; no layout or style normalization."""
import codecs


def differences(left, right, path='$'):
    if type(left) is not type(right):
        yield dict(path=path, pi=left, pisharp=right)
    elif isinstance(left, dict):
        for key in sorted(left.keys() | right.keys()):
            if key not in left or key not in right:
                yield dict(path=f'{path}.{key}', pi=left.get(key), pisharp=right.get(key))
            else:
                yield from differences(left[key], right[key], f'{path}.{key}')
    elif isinstance(left, list):
        if len(left) != len(right):
            yield dict(path=f'{path}.length', pi=len(left), pisharp=len(right))
        for index, (first, second) in enumerate(zip(left, right)):
            yield from differences(first, second, f'{path}[{index}]')
    elif left != right:
        yield dict(path=path, pi=left, pisharp=right)


class ControlTrace:
    def __init__(self):
        self.events = []
        self.pending = bytearray()
        self.kind = None
        self.introducer = None
        self.decoder = codecs.getincrementaldecoder('utf-8')('surrogateescape')

    def write(self, data):
        for character in self.decoder.decode(data):
            byte = ord(character)
            if 0xdc80 <= byte <= 0xdcff:
                byte -= 0xdc00
            encoded = character.encode('utf-8', 'surrogateescape')
            if not self.pending:
                if byte == 0x1b:
                    self.pending.extend(encoded)
                    self.kind = 'escape'
                elif byte in [0x9b, 0x90, 0x98, 0x9d, 0x9e, 0x9f]:
                    self.pending.extend(encoded)
                    self.kind = 'csi' if byte == 0x9b else 'string'
                    self.introducer = byte
                elif byte < 0x20 or 0x7f <= byte <= 0x9f:
                    self.events.append(encoded.hex())
                continue
            self.pending.extend(encoded)
            if self.kind == 'escape':
                if byte == ord('['):
                    self.kind = 'csi'
                elif byte in [ord(value) for value in ']PX^_']:
                    self.kind = 'string'
                    self.introducer = byte
                elif not 0x20 <= byte <= 0x2f:
                    self.finish()
            elif self.kind == 'csi' and 0x40 <= byte <= 0x7e:
                self.finish()
            elif self.kind == 'string' and ((byte == 0x07 and self.introducer in [ord(']'), 0x9d]) or byte == 0x9c or self.pending[-2:] == b'\x1b\\'):
                self.finish()

    def finish(self):
        self.events.append(self.pending.hex())
        self.pending.clear()
        self.kind = None
