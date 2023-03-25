﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace opentuner
{
    public delegate void NimStatusCallback(NimStatus status);

    public class NimThread
    {
        ftdi hardware;

        nim _nim;
        stv0910 _stv0910;
        stv6120 _stv6120;
        stvvglna stvvglna_top;
        stvvglna stvvglna_bottom;

        ConcurrentQueue<NimConfig> config_queue;
        NimStatusCallback status_callback = null;

        bool lna_top_ok = false;
        bool lna_bottom_ok = false;
        bool reset = false;

        public event EventHandler<StatusEvent> onNewStatus;

        public NimThread(ConcurrentQueue<NimConfig> _config_queue, ftdi _hardware, NimStatusCallback _status_callback)
        {
            hardware = _hardware;
            config_queue = _config_queue;
            status_callback = _status_callback;

            _nim = new nim(hardware);

            _stv0910 = new stv0910(_nim);
            _stv6120 = new stv6120(_nim);
            stvvglna_top = new stvvglna(_nim);
            stvvglna_bottom = new stvvglna(_nim);
        }

        // https://wiki.batc.org.uk/MiniTiouner_Power_Level_Indication
        short get_rf_level(ushort agc1, ushort agc2)
        {
            int index = -1;

            if (agc1 >= 0)
            {
                index = lookups.agc1_lookup.BinarySearch(agc1);

                if (index < 0)
                    index = ~index;
            }
            else
            {
                index = lookups.agc2_lookup.BinarySearch(agc2);

                if (index < 0)
                    index = ~index;

            }

            if (index < 0) index = 0;

            if (index > lookups.rf_power_level.Count())
                index = lookups.rf_power_level.Count() - 1;

            return lookups.rf_power_level[index];
        }

        byte get_nim_status()
        {
            NimStatus nim_status = new NimStatus();

            byte err = 0;

            nim_status.reset = reset;

            // get scan state (demod state)
            byte demod_state = 0;
            err = _stv0910.stv0910_read_scan_state(stv0910.STV0910_DEMOD_TOP, ref demod_state);
            nim_status.demod_status = demod_state;

            // get lna info
            nim_status.lna_bottom_ok = lna_bottom_ok;
            nim_status.lna_top_ok = lna_top_ok;

            byte lna_gain = 0, lna_vgo = 0;
            if (err == 0) stvvglna_top.stvvglna_read_agc(nim.NIM_INPUT_TOP, ref lna_gain, ref lna_vgo);
            nim_status.lna_gain = (ushort)((lna_gain << 5) | lna_vgo);

            // power
            byte power_i = 0;
            byte power_q = 0;
            if (err == 0) err = _stv0910.stv0910_read_power(stv0910.STV0910_DEMOD_TOP, ref power_i, ref power_q);
            nim_status.power_i = power_i;
            nim_status.power_q = power_q;


            byte[,] constellation_data = new byte[16, 2];

            byte con_i = 0;
            byte con_q = 0;
            if (err == 0)
            {
                for (byte count = 0; count < 16; count++)
                {
                    _stv0910.stv0910_read_constellation(stv0910.STV0910_DEMOD_TOP, ref con_i, ref con_q);
                    constellation_data[count,0] = con_i;
                    constellation_data[count,1] = con_q;
                }
            }

            nim_status.constellation = constellation_data;

            /* puncture rate */
            byte puncture_rate = 0;
            if (err == 0) err = _stv0910.stv0910_read_puncture_rate(stv0910.STV0910_DEMOD_TOP, ref puncture_rate);
            nim_status.puncture_rate = puncture_rate;

            /* carrier frequency offset we are trying */
            Int32 frequency_offset = 0;
            if (err == 0) err = _stv0910.stv0910_read_car_freq(stv0910.STV0910_DEMOD_TOP, ref frequency_offset);
            nim_status.frequency_carrier_offset = frequency_offset;

            /* symbol rate we are trying */
            UInt32 sr = 0;
            if (err == 0) err = _stv0910.stv0910_read_sr(stv0910.STV0910_DEMOD_TOP, ref sr);
            nim_status.symbol_rate = sr;

            /* viterbi error rate */
            UInt32 viterbi_error_rate = 0;
            if (err == 0) err = _stv0910.stv0910_read_err_rate(stv0910.STV0910_DEMOD_TOP, ref viterbi_error_rate);
            nim_status.viterbi_error_rate = viterbi_error_rate;

            /* BER */
            UInt32 ber = 0;
            if (err == 0) err = _stv0910.stv0910_read_ber(stv0910.STV0910_DEMOD_TOP, ref ber);
            nim_status.ber = ber;

            /* BCH Uncorrected Flag */
            bool errors_bch_uncorrected = false;
            if (err == 0) err = _stv0910.stv0910_read_errors_bch_uncorrected(stv0910.STV0910_DEMOD_TOP, ref errors_bch_uncorrected);
            nim_status.errors_bch_uncorrected = errors_bch_uncorrected;

            /* BCH Error Count */
            UInt32 errors_bch_count = 0;
            if (err == 0) err = _stv0910.stv0910_read_errors_bch_count(stv0910.STV0910_DEMOD_TOP, ref errors_bch_count);
            nim_status.errors_bch_count = errors_bch_count;

            /* LDPC Error Count */
            UInt32 errors_ldpc_count = 0;
            if (err == 0) err = _stv0910.stv0910_read_errors_ldpc_count(stv0910.STV0910_DEMOD_TOP, ref errors_ldpc_count);
            nim_status.errors_ldpc_count = errors_ldpc_count;

            // agc1 gain
            ushort agc1_gain = 0;
            if (err == 0) err = _stv0910.stv0910_read_agc1_gain(stv0910.STV0910_DEMOD_TOP, ref agc1_gain);
            nim_status.agc1_gain = agc1_gain;

            // agc2 gain
            ushort agc2_gain = 0;
            if (err == 0) err = _stv0910.stv0910_read_agc2_gain(stv0910.STV0910_DEMOD_TOP, ref agc2_gain);
            nim_status.agc2_gain = agc2_gain;

            //Console.WriteLine(nim_status.agc1_gain.ToString() + "," + nim_status.agc2_gain.ToString());

            nim_status.input_power_level = get_rf_level(agc1_gain, agc2_gain);

            UInt32 mer = 0;

            if (demod_state == stv0910.DEMOD_S || demod_state == stv0910.DEMOD_S2)
            {
                if (err == 0) err = _stv0910.stv0910_read_mer(stv0910.STV0910_DEMOD_TOP, ref mer);
            }

            nim_status.mer = mer;

            /* MODCOD, Short Frames, Pilots */
            UInt32 modcod = 0;
            bool short_frame = false;
            bool pilots = false;
            if (err == 0) err = _stv0910.stv0910_read_modcod_and_type(stv0910.STV0910_DEMOD_TOP, ref modcod, ref short_frame, ref pilots);

            nim_status.modcode = modcod;

            if (demod_state != stv0910.DEMOD_S2)
            {
                /* short frames & pilots only valid for S2 DEMOD state */
                nim_status.short_frame = false;
                nim_status.pilots = false;
            }

            if (onNewStatus != null)
            {
                StatusEvent new_nim_status = new StatusEvent();
                new_nim_status.nim_status = nim_status;
                onNewStatus(this, new_nim_status);
            }


            // send status callback if available
            status_callback?.Invoke(nim_status);


            reset = false;

            return err;
        }

        public void worker_thread()
        {
            int hw_errors = 0;

            try
            {

                Console.WriteLine("Nim Thread: Starting...");

            bool initialConfig = false;

            NimConfig nim_config = null;

                while (true)
                {
                    if (initialConfig == false)
                    {
                        Console.WriteLine("Nim Thread: Initial Config");
                    }

                    if (config_queue.Count() > 0 || initialConfig == false)
                    {
                        while (config_queue.TryDequeue(out nim_config))
                        {
                            Thread.Sleep(10);

                            Console.WriteLine("Init Nim");
                            byte err = _nim.nim_init();

                            // init demod

                            if (err == 0)
                            {
                                Console.WriteLine("Init Demod");
                                err = _stv0910.stv0910_init(nim_config.symbol_rate, 0);
                            }
                            else
                            {
                                Console.WriteLine("Error before Demod");
                            }

                            // init tuner
                            if (err == 0)
                            {
                                Console.WriteLine("Init Tuner");
                                err = _stv6120.stv6120_init(nim_config.frequency, 0, nim_config.rf_input_B);
                            }
                            else
                            {
                                Console.WriteLine("Error before Tuner");
                            }

                            // init lna - if found
                            if (err == 0)
                            {
                                Console.WriteLine("Init LNA Top");
                                lna_top_ok = false;
                                err = stvvglna_top.stvvglna_init(nim.NIM_INPUT_TOP, stvvglna.STVVGLNA_ON, ref lna_top_ok);
                            }
                            else
                            {
                                Console.WriteLine("Error before Lna Top");
                            }

                            // init lna - if found
                            if (err == 0)
                            {
                                Console.WriteLine("Init LNA Bottom");
                                lna_bottom_ok = false;
                                err = stvvglna_bottom.stvvglna_init(nim.NIM_INPUT_BOTTOM, stvvglna.STVVGLNA_OFF, ref lna_bottom_ok);
                            }
                            else
                            {
                                Console.WriteLine("Error before Lna Bottom");
                            }


                            // demod - start scan
                            if (err == 0)
                            {
                                Console.WriteLine("Demod Start Scan");
                                err = _stv0910.stv0910_start_scan(stv0910.STV0910_DEMOD_TOP);
                            }
                            else
                            {
                                Console.WriteLine("Error before demod scan");
                            }

                            // lnb power supply
                            if (err  == 0)
                            {
                                if (nim_config.polarization_supply)
                                {
                                    hardware.ftdi_set_polarization_supply(0, true, nim_config.polarization_supply_horizontal);
                                }
                                else
                                {
                                    hardware.ftdi_set_polarization_supply(0, false, false);
                                }

                            }
                            else
                            {
                                Console.WriteLine("Error lnb pwer");
                            }

                            // done, if we have errors, then exit thread
                            if (err != 0)
                            {
                                Console.WriteLine("****** Nim Thread: Hardware Error: " + err.ToString() + " ******");
                                hw_errors += 1;
                                if (hw_errors > 5)
                                {
                                    Console.WriteLine("Too many hardware errors");
                                    return;
                                }
                            }
                            else
                            {
                                Console.WriteLine("Nim Thread: Nim Init Good");
                            }

                            initialConfig = true;
                            reset = true;
                        }
                    }
                    else
                    {
                        get_nim_status();
                        Thread.Sleep(200);
                    }
                }
            }
            catch (ThreadAbortException)
            {
                Console.WriteLine("Nim Thread: Closing");
            }

        }

    }

    public class StatusEvent : EventArgs
    {
        public NimStatus nim_status { get; set; }
    }
}
