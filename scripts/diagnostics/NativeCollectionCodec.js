'use strict';

globalThis.createCollectionEncoder = function () {
    const recent = new Map();
    return {
        entries() { return recent.size; },
        encode(regions) {
            const blocks = [];
            const chunks = [];
            const additions = new Set();
            let offset = 0;
            let wireOffset = 0;
            const descriptors = regions.map(region => {
                const descriptor = { ...region };
                delete descriptor.bytes;
                if (region.bytes === undefined) return descriptor;
                if (region.bytes.byteLength !== region.size) throw new Error('Invalid collection region length');
                descriptor.offset = offset;
                offset += region.size;
                for (let start = 0; start < region.size; start += 65536) {
                    const bytes = region.bytes.slice(start, Math.min(start + 65536, region.size));
                    const sha256 = Checksum.compute('sha256', bytes);
                    const chunk = { sha256, size: bytes.byteLength };
                    if (recent.has(sha256)) {
                        recent.delete(sha256);
                        recent.set(sha256, true);
                    } else {
                        chunk.wireOffset = wireOffset;
                        wireOffset += bytes.byteLength;
                        blocks.push(bytes);
                        additions.add(sha256);
                    }
                    chunks.push(chunk);
                }
                return descriptor;
            });
            const wire = new Uint8Array(wireOffset);
            let cursor = 0;
            for (const block of blocks) {
                wire.set(new Uint8Array(block), cursor);
                cursor += block.byteLength;
            }
            for (const sha256 of additions) {
                recent.set(sha256, true);
                if (recent.size > 4096) recent.delete(recent.keys().next().value);
            }
            return { wire: wire.buffer, fields: { encoding: 'sha256-chunks-v1',
                byteLength: offset, wireByteLength: wireOffset, regions: descriptors, chunks } };
        }
    };
};
