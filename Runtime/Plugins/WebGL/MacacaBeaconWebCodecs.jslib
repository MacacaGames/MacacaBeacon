mergeInto(LibraryManager.library, {
  $MacacaBeaconWebCodecs: {
    nextId: 0,
    sessions: {},
    lastError: '',

    box: function (type) {
      var size = 8;
      for (var i = 1; i < arguments.length; i++) size += arguments[i].length;
      var out = new Uint8Array(size);
      var view = new DataView(out.buffer);
      view.setUint32(0, size);
      for (var c = 0; c < 4; c++) out[4 + c] = type.charCodeAt(c);
      var offset = 8;
      for (var j = 1; j < arguments.length; j++) {
        out.set(arguments[j], offset);
        offset += arguments[j].length;
      }
      return out;
    },

    concat: function (parts) {
      var length = 0;
      for (var i = 0; i < parts.length; i++) length += parts[i].length;
      var out = new Uint8Array(length);
      var offset = 0;
      for (var j = 0; j < parts.length; j++) {
        out.set(parts[j], offset);
        offset += parts[j].length;
      }
      return out;
    },

    u8: function (values) { return new Uint8Array(values); },
    u16: function (value) {
      var bytes = new Uint8Array(2);
      new DataView(bytes.buffer).setUint16(0, value);
      return bytes;
    },
    u32: function (value) {
      var bytes = new Uint8Array(4);
      new DataView(bytes.buffer).setUint32(0, value);
      return bytes;
    },
    str: function (value) {
      var bytes = new Uint8Array(value.length);
      for (var i = 0; i < value.length; i++) bytes[i] = value.charCodeAt(i);
      return bytes;
    },

    createSession: function (id, path, width, height, fps, bitrate) {
      var session = {
        id: id,
        path: path,
        width: width,
        height: height,
        fps: fps,
        bitrate: bitrate,
        frameTimestamps: [],
        pending: [],
        done: false,
        encoder: null,
        muxer: {
          width: width,
          height: height,
          fps: fps,
          duration: 0,
          samples: [],
          description: null
        }
      };

      session.encoder = new VideoEncoder({
        output: function (chunk, metadata) {
          if (metadata && metadata.decoderConfig && metadata.decoderConfig.description) {
            session.muxer.description = new Uint8Array(metadata.decoderConfig.description);
          }
          var data = new Uint8Array(chunk.byteLength);
          chunk.copyTo(data);
          session.muxer.samples.push({
            data: data,
            timestamp: chunk.timestamp || 0,
            key: chunk.type === 'key'
          });
        },
        error: function (error) {
          MacacaBeaconWebCodecs.lastError = error.message || String(error);
          session.done = true;
        }
      });
      session.encoder.configure({
        codec: 'avc1.42001f',
        width: width,
        height: height,
        bitrate: bitrate,
        framerate: fps,
        hardwareAcceleration: 'prefer-hardware',
        avc: { format: 'avc' }
      });

      session.addJpeg = function (bytes, timestamp) {
        var work = createImageBitmap(new Blob([bytes], { type: 'image/jpeg' })).then(function (bitmap) {
          var frame = new VideoFrame(bitmap, { timestamp: timestamp });
          session.encoder.encode(frame, { keyFrame: session.frameTimestamps.length === 0 });
          frame.close();
          bitmap.close();
          session.frameTimestamps.push(timestamp);
        });
        session.pending.push(work);
      };

      session.addRgba = function (bytes, frameWidth, frameHeight, timestamp) {
        var frame = new VideoFrame(bytes, {
          format: 'RGBA',
          codedWidth: frameWidth,
          codedHeight: frameHeight,
          timestamp: timestamp
        });
        session.encoder.encode(frame, { keyFrame: session.frameTimestamps.length === 0 });
        frame.close();
        session.frameTimestamps.push(timestamp);
      };

      session.finish = function (duration) {
        session.muxer.duration = duration;
        if (session.done) return;
        var timeout = new Promise(function (_, reject) {
          setTimeout(function () { reject(new Error('WebCodecs flush timed out.')); }, 30000);
        });
        Promise.race([Promise.all(session.pending), timeout])
          .then(function () { return session.encoder.flush(); })
          .then(function () {
            if (!session.muxer.description) {
              throw new Error('WebCodecs did not return an AVC decoder configuration.');
            }
            if (session.muxer.samples.length === 0) {
              throw new Error('WebCodecs produced no H.264 samples.');
            }
            FS.writeFile(session.path, MacacaBeaconWebCodecs.makeMp4(session.muxer));
            session.encoder.close();
            session.done = true;
          })
          .catch(function (error) {
            MacacaBeaconWebCodecs.lastError = error.message || String(error);
            session.done = true;
          });
      };

      return session;
    },

    makeMp4: function (muxer) {
      var samples = muxer.samples;
      var media = [];
      var durations = [];
      for (var i = 0; i < samples.length; i++) media.push(samples[i].data);
      for (var d = 0; d < samples.length; d++) {
        var nextTimestamp = d + 1 < samples.length
          ? samples[d + 1].timestamp
          : Math.max(samples[d].timestamp + 1000000 / muxer.fps, muxer.duration);
        durations.push(Math.max(1, Math.round(nextTimestamp - samples[d].timestamp)));
      }
      var ftyp = MacacaBeaconWebCodecs.box(
        'ftyp',
        MacacaBeaconWebCodecs.str('isom'),
        MacacaBeaconWebCodecs.u32(0x200),
        MacacaBeaconWebCodecs.str('isomiso2avc1mp41'));
      var mdat = MacacaBeaconWebCodecs.box('mdat', MacacaBeaconWebCodecs.concat(media));
      var moov = MacacaBeaconWebCodecs.makeMoov(muxer, durations, 0);
      moov = MacacaBeaconWebCodecs.makeMoov(muxer, durations, ftyp.length + moov.length + 8);
      return MacacaBeaconWebCodecs.concat([ftyp, moov, mdat]);
    },

    makeMoov: function (muxer, durations, dataOffset) {
      var total = 0;
      for (var i = 0; i < durations.length; i++) total += durations[i];

      var stts = MacacaBeaconWebCodecs.box(
        'stts', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(durations.length));
      for (var s = 0; s < durations.length; s++) {
        stts = MacacaBeaconWebCodecs.concat([
          stts, MacacaBeaconWebCodecs.u32(1), MacacaBeaconWebCodecs.u32(durations[s])]);
      }

      var stsz = MacacaBeaconWebCodecs.box(
        'stsz', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(muxer.samples.length));
      for (var z = 0; z < muxer.samples.length; z++) {
        stsz = MacacaBeaconWebCodecs.concat([stsz, MacacaBeaconWebCodecs.u32(muxer.samples[z].data.length)]);
      }

      var sync = [];
      for (var k = 0; k < muxer.samples.length; k++) {
        if (muxer.samples[k].key) sync.push(k + 1);
      }
      if (sync.length === 0 && muxer.samples.length > 0) sync.push(1);
      var stss = MacacaBeaconWebCodecs.box(
        'stss', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(sync.length));
      for (var q = 0; q < sync.length; q++) {
        stss = MacacaBeaconWebCodecs.concat([stss, MacacaBeaconWebCodecs.u32(sync[q])]);
      }

      var avc1 = MacacaBeaconWebCodecs.box(
        'avc1',
        MacacaBeaconWebCodecs.u8([0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]),
        MacacaBeaconWebCodecs.u16(muxer.width), MacacaBeaconWebCodecs.u16(muxer.height),
        MacacaBeaconWebCodecs.u32(0x00480000), MacacaBeaconWebCodecs.u32(0x00480000),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u16(1),
        MacacaBeaconWebCodecs.u8(new Array(32).fill(0)),
        MacacaBeaconWebCodecs.u16(0x0018), MacacaBeaconWebCodecs.u16(0xffff),
        MacacaBeaconWebCodecs.box('avcC', muxer.description),
        MacacaBeaconWebCodecs.box('btrt', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0)));
      var stsd = MacacaBeaconWebCodecs.box(
        'stsd', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(1), avc1);
      var stsc = MacacaBeaconWebCodecs.box(
        'stsc', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(1),
        MacacaBeaconWebCodecs.u32(1), MacacaBeaconWebCodecs.u32(muxer.samples.length),
        MacacaBeaconWebCodecs.u32(1));
      var stco = MacacaBeaconWebCodecs.box(
        'stco', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(1),
        MacacaBeaconWebCodecs.u32(dataOffset));
      var stbl = MacacaBeaconWebCodecs.box('stbl', stsd, stts, stsc, stsz, stco, stss);
      var dinf = MacacaBeaconWebCodecs.box(
        'dinf', MacacaBeaconWebCodecs.box(
          'dref', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(1),
          MacacaBeaconWebCodecs.box('url ', MacacaBeaconWebCodecs.u32(1))));
      var vmhd = MacacaBeaconWebCodecs.box(
        'vmhd', MacacaBeaconWebCodecs.u32(1), MacacaBeaconWebCodecs.u16(0),
        MacacaBeaconWebCodecs.u16(0), MacacaBeaconWebCodecs.u16(0), MacacaBeaconWebCodecs.u16(0));
      var minf = MacacaBeaconWebCodecs.box('minf', vmhd, dinf, stbl);
      var mdhd = MacacaBeaconWebCodecs.box(
        'mdhd', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(1000000),
        MacacaBeaconWebCodecs.u32(total), MacacaBeaconWebCodecs.u16(0x55c4),
        MacacaBeaconWebCodecs.u16(0));
      var hdlr = MacacaBeaconWebCodecs.box(
        'hdlr', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.str('vide'), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.str('VideoHandler\0'));
      var mdia = MacacaBeaconWebCodecs.box('mdia', mdhd, hdlr, minf);
      var tkhd = MacacaBeaconWebCodecs.box(
        'tkhd', MacacaBeaconWebCodecs.u32(3), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(1),
        MacacaBeaconWebCodecs.u32(total), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u16(0),
        MacacaBeaconWebCodecs.u16(0), MacacaBeaconWebCodecs.u32(0x00010000),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0x00010000),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0x40000000),
        MacacaBeaconWebCodecs.u32(muxer.width << 16),
        MacacaBeaconWebCodecs.u32(muxer.height << 16));
      var trak = MacacaBeaconWebCodecs.box('trak', tkhd, mdia);
      var mvhd = MacacaBeaconWebCodecs.box(
        'mvhd', MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(1000000),
        MacacaBeaconWebCodecs.u32(total), MacacaBeaconWebCodecs.u32(0x00010000),
        MacacaBeaconWebCodecs.u16(0x0100), MacacaBeaconWebCodecs.u16(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0x00010000), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0x00010000), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0), MacacaBeaconWebCodecs.u32(0),
        MacacaBeaconWebCodecs.u32(0x40000000), MacacaBeaconWebCodecs.u32(2));
      return MacacaBeaconWebCodecs.box('moov', mvhd, trak);
    }
  },

  MacacaBeaconWebCodecs_IsAvailable__deps: ['$MacacaBeaconWebCodecs'],
  MacacaBeaconWebCodecs_IsAvailable: function () {
    return typeof VideoEncoder !== 'undefined' &&
      typeof VideoFrame !== 'undefined' &&
      typeof createImageBitmap !== 'undefined';
  },

  MacacaBeaconWebCodecs_Begin__deps: ['$MacacaBeaconWebCodecs'],
  MacacaBeaconWebCodecs_Begin: function (outputPath, width, height, fps, bitrate) {
    try {
      var path = UTF8ToString(outputPath);
      MacacaBeaconWebCodecs.lastError = '';
      var id = ++MacacaBeaconWebCodecs.nextId;
      MacacaBeaconWebCodecs.sessions[id] = MacacaBeaconWebCodecs.createSession(
        id, path, width, height, fps, bitrate);
      return id;
    } catch (error) {
      MacacaBeaconWebCodecs.lastError = error.message || String(error);
      return 0;
    }
  },

  MacacaBeaconWebCodecs_AddJpeg__deps: ['$MacacaBeaconWebCodecs'],
  MacacaBeaconWebCodecs_AddJpeg: function (id, ptr, length, seconds) {
    var session = MacacaBeaconWebCodecs.sessions[id];
    if (!session) return 0;
    try {
      session.addJpeg(HEAPU8.slice(ptr, ptr + length), seconds * 1000000);
      return 1;
    } catch (error) {
      MacacaBeaconWebCodecs.lastError = error.message || String(error);
      return 0;
    }
  },

  MacacaBeaconWebCodecs_AddRgba__deps: ['$MacacaBeaconWebCodecs'],
  MacacaBeaconWebCodecs_AddRgba: function (id, ptr, length, width, height, seconds) {
    var session = MacacaBeaconWebCodecs.sessions[id];
    if (!session) return 0;
    try {
      session.addRgba(HEAPU8.slice(ptr, ptr + length), width, height, seconds * 1000000);
      return 1;
    } catch (error) {
      MacacaBeaconWebCodecs.lastError = error.message || String(error);
      return 0;
    }
  },

  MacacaBeaconWebCodecs_Finish__deps: ['$MacacaBeaconWebCodecs'],
  MacacaBeaconWebCodecs_Finish: function (id, durationSeconds) {
    var session = MacacaBeaconWebCodecs.sessions[id];
    if (session) session.finish(durationSeconds * 1000000);
  },

  MacacaBeaconWebCodecs_IsDone__deps: ['$MacacaBeaconWebCodecs'],
  MacacaBeaconWebCodecs_IsDone: function (id) {
    var session = MacacaBeaconWebCodecs.sessions[id];
    return session && session.done ? 1 : 0;
  },

  MacacaBeaconWebCodecs_LastError__deps: ['$MacacaBeaconWebCodecs'],
  MacacaBeaconWebCodecs_LastError: function () {
    var error = MacacaBeaconWebCodecs.lastError || '';
    var length = lengthBytesUTF8(error) + 1;
    var pointer = _malloc(length);
    stringToUTF8(error, pointer, length);
    return pointer;
  },

  MacacaBeaconWebCodecs_Destroy__deps: ['$MacacaBeaconWebCodecs'],
  MacacaBeaconWebCodecs_Destroy: function (id) {
    var session = MacacaBeaconWebCodecs.sessions[id];
    if (session && session.encoder && session.encoder.state !== 'closed') {
      try { session.encoder.close(); } catch (_) { }
    }
    delete MacacaBeaconWebCodecs.sessions[id];
  }
});
